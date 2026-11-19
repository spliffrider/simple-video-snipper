using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleVideoSnipper;

internal sealed class SaveWorkflow(RecordingStorage storage, PreferencesStore store,
    SavePreferences initialPreferences, NotifyIcon tray, GlobalCaptureShortcut shortcut) : IDisposable
{
    private readonly Queue<(PendingClip Clip, bool Ask)> queue = new();
    private readonly HashSet<string> queuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private SavePreferences preferences = initialPreferences;
    private SavedRecordingNotice? notice;
    private bool processing;
    private bool choosing;

    public bool Busy => processing || choosing;
    public string SaveFolder => preferences.Folder;

    public void Start(string? warning)
    {
        if (warning is not null)
            Notify("Saving preferences", warning);
        var count = Pending().Length;
        if (count > 0)
            Notify("Unsaved recordings", $"You have {count} unsaved recording{(count == 1 ? "" : "s")}. Open Unsaved recordings from the tray to continue.");
    }

    public void ShowSettings()
    {
        if (Busy)
            return;
        choosing = true;
        try
        {
            using var dialog = new SaveSettingsDialog(preferences, storage.CurrentFolder);
            while (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var next = storage.WithFolder(dialog.Preferences);
                    if (storage.IsInPendingFolder(next.Folder, next))
                        throw new IOException("Choose a save folder outside the temporary recording files.");
                    if (!shortcut.TryApply(next.SnipShortcut, () => store.Save(next)))
                    {
                        dialog.ShowShortcutError($"{next.SnipShortcut} is unavailable. Choose another shortcut.");
                        continue;
                    }
                    storage.Apply(next);
                    preferences = next;
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    MessageBox.Show(exception.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        finally
        {
            choosing = false;
        }
    }

    public void ShowPending()
    {
        if (Busy)
            return;
        choosing = true;
        try
        {
            PickPending();
        }
        finally
        {
            choosing = false;
        }
    }

    private void PickPending()
    {
        var clips = Pending();
        if (clips.Length == 0)
        {
            MessageBox.Show("There are no unsaved recordings.", "Unsaved recordings");
            return;
        }

        using var picker = new Form
        {
            Text = "Unsaved recordings", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen, AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Width = 400, Height = 180, AccessibleName = "Unsaved recordings" };
        list.Items.AddRange(clips);
        list.SelectedIndex = 0;
        var open = new Button { Text = "Continue", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var discard = new Button { Text = "Discard", AutoSize = true };
        discard.Click += (_, _) =>
        {
            if (list.SelectedItem is not PendingClip clip ||
                MessageBox.Show(picker, $"Discard {clip.Title}?", picker.Text, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (!RecordingExporter.TryDelete(clip.Path))
            {
                MessageBox.Show(picker, "The temporary file could not be removed. Try again later.", picker.Text);
                return;
            }
            list.Items.Remove(clip);
            if (list.Items.Count > 0)
                list.SelectedIndex = 0;
            else
                picker.DialogResult = DialogResult.Cancel;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([open, discard, close]);
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12),
        };
        layout.Controls.AddRange([list, buttons]);
        picker.Controls.Add(layout);
        picker.AcceptButton = open;
        picker.CancelButton = close;
        if (picker.ShowDialog() == DialogResult.OK && list.SelectedItem is PendingClip chosen)
            Enqueue(chosen, ask: true);
    }

    private PendingClip[] Pending()
    {
        var clips = new List<PendingClip>();
        foreach (var folder in storage.PendingFolders())
        {
            if (!Directory.Exists(folder))
                continue;
            try
            {
                clips.AddRange(Directory.EnumerateFiles(folder).Select(PendingClip.FromFile).OfType<PendingClip>());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Notify("Unsaved recordings", "A temporary recordings folder is unavailable.");
            }
        }
        return clips.OrderByDescending(clip => clip.Title, StringComparer.CurrentCulture).ToArray();
    }

    public void Enqueue(PendingClip clip, bool ask = false)
    {
        if (queuedPaths.Add(clip.Path))
            queue.Enqueue((clip, ask));
    }

    public async void ProcessReady()
    {
        if (Busy || queue.Count == 0)
            return;

        processing = true;
        try
        {
            while (queue.TryDequeue(out var item))
            {
                try
                {
                    if (File.Exists(item.Clip.Path))
                        await SaveOneAsync(item.Clip, item.Ask);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message, "Could not process recording", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    queuedPaths.Remove(item.Clip.Path);
                }
            }
        }
        finally
        {
            processing = false;
        }
    }

    private async Task SaveOneAsync(PendingClip clip, bool ask)
    {
        var settings = preferences;
        ClipEdits? edits = null;
        while (true)
        {
            var save = new TaskDialogButton("Save");
            var edit = new TaskDialogButton("Edit");
            var later = new TaskDialogButton("Keep for later");
            var action = TaskDialog.ShowDialog(new TaskDialogPage
            {
                Caption = "Simple Video Snipper", Heading = "Recording ready", Text = clip.Title,
                Buttons = { save, edit, later }, AllowCancel = true, DefaultButton = save,
            });
            if (action == save)
                break;
            if (action != edit)
            {
                NotifyKept();
                return;
            }

            try
            {
                tray.Text = "Simple Video Snipper — Opening editor";
                using var media = await ClipEditMedia.OpenAsync(clip);
                using var editor = new ClipEditor(clip.Title, media);
                var editResult = editor.ShowDialog();
                await editor.StopPreviewAsync();
                if (editResult != DialogResult.OK)
                    continue;
                edits = editor.Edits;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(exception.Message, "Could not open editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                tray.Text = "Simple Video Snipper";
            }
        }

        ExportRequest? request = ask || settings.Mode == SaveMode.Ask
            ? ChooseFile(clip, settings)
            : ExportRequest.Quick(clip, settings);
        while (request is { } destination)
        {
            ExportResult result;
            try
            {
                if (storage.IsInPendingFolder(destination.Path))
                    throw new IOException("Choose a save location outside the temporary recording files.");
                tray.Text = "Simple Video Snipper — Saving recording";
                result = await Task.Run(() => RecordingExporter.SaveAsync(clip, destination with { Edits = edits }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                request = ChooseRetry(clip, settings, destination, exception.Message);
                continue;
            }
            finally
            {
                tray.Text = "Simple Video Snipper";
            }

            notice?.Dispose();
            notice = new SavedRecordingNotice(result, settings.AutoCopy);
            NativeCaptureMethods.ExcludeWindow(notice);
            notice.Show();
            return;
        }
        NotifyKept();
    }

    private static ExportRequest? ChooseFile(PendingClip clip, SavePreferences settings, ExportRequest? previous = null)
    {
        var format = previous?.Format ?? settings.Format;
        var folder = previous is { } request ? Path.GetDirectoryName(request.Path) : settings.Folder;
        if (folder == SavePreferences.DefaultFolder && !Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The picker can still offer another directory.
            }
        }
        while (true)
        {
            using var picker = new SaveFileDialog
            {
                Title = "Save recording as", Filter = ExportFormats.Filter, FilterIndex = format == ExportFormat.WebM ? 2 : 1,
                InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : "",
                FileName = previous is { } failed ? Path.GetFileNameWithoutExtension(failed.Path) : clip.Title,
                AddExtension = true, CheckPathExists = true, OverwritePrompt = false, RestoreDirectory = true,
            };
            if (picker.ShowDialog() != DialogResult.OK)
                return null;
            var selected = ExportFormats.FromPath(picker.FileName) ?? (picker.FilterIndex == 2 ? ExportFormat.WebM : ExportFormat.Mp4);
            var path = Path.ChangeExtension(picker.FileName, selected.Extension());
            var replace = File.Exists(path);
            if (!replace || MessageBox.Show($"Replace {Path.GetFileName(path)}?", "Save recording",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                return new ExportRequest(path, selected, replace);
            previous = new ExportRequest(path, selected);
            folder = Path.GetDirectoryName(path);
            format = selected;
        }
    }

    private static ExportRequest? ChooseRetry(PendingClip clip, SavePreferences settings, ExportRequest failed, string error)
    {
        var retry = new TaskDialogButton("Retry");
        var saveAs = new TaskDialogButton("Save As…");
        var later = new TaskDialogButton("Keep for later");
        var choice = TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "Simple Video Snipper", Heading = "Could not save the recording",
            Text = error + Environment.NewLine + Environment.NewLine + "The temporary recording is still available.",
            Icon = TaskDialogIcon.Error, Buttons = { retry, saveAs, later }, AllowCancel = true,
        });
        if (choice == retry)
            return failed;
        return choice == saveAs ? ChooseFile(clip, settings, failed) : null;
    }

    public void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(SaveFolder);
            Process.Start(new ProcessStartInfo(SaveFolder) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            MessageBox.Show(exception.Message, "Could not open the save folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Notify(string title, string message) => tray.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
    private void NotifyKept() => Notify("Recording kept", "Open Unsaved recordings from the tray to save it later.");
    public void Dispose() => notice?.Dispose();
}

internal sealed class SavedRecordingNotice : Form
{
    private readonly Label state = new() { AutoSize = true, MaximumSize = new Size(430, 0) };
    private readonly string path;
    private readonly string? cleanupWarning;

    public SavedRecordingNotice(ExportResult result, bool autoCopy)
    {
        path = result.Path;
        cleanupWarning = result.CleanupWarning;
        Text = "Recording saved";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var open = new Button { Text = "Open", AutoSize = true };
        var folder = new Button { Text = "Show in folder", AutoSize = true };
        var copy = new Button { Text = "Copy video file", AutoSize = true };
        open.Click += (_, _) => Launch(new ProcessStartInfo(path) { UseShellExecute = true });
        folder.Click += (_, _) => Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        copy.Click += (_, _) => Copy();
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        actions.Controls.AddRange([open, folder, copy]);
        var layout = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12),
        };
        layout.Controls.AddRange([state, actions]);
        Controls.Add(layout);
        if (autoCopy)
            Copy();
        else
            SetStatus("Saved");
    }

    protected override bool ShowWithoutActivation => true;

    private void SetStatus(string status) => state.Text = string.Join(Environment.NewLine,
        new[] { status, path, cleanupWarning }.OfType<string>());

    private void Copy()
    {
        try
        {
            Clipboard.SetDataObject(FileData(path), copy: true, retryTimes: 5, retryDelay: 80);
            SetStatus("Copied the video file");
        }
        catch (ExternalException)
        {
            SetStatus("Saved. Clipboard is busy. Use Copy video file to retry.");
        }
    }

    internal static DataObject FileData(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        return data;
    }

    private void Launch(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start);
        }
        catch (Win32Exception exception)
        {
            SetStatus(exception.Message);
        }
    }
}
