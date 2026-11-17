using System.ComponentModel;
using System.Diagnostics;

namespace SimpleVideoSnipper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleVideoSnipper");

        try
        {
            var pendingFolder = Path.Combine(appData, "Recordings", "Pending");
            using var tray = new RecordingTray(RecordingHost.Start(pendingFolder),
                new PreferencesStore(Path.Combine(appData, "settings.json")), args.Contains("--open"));
            Application.Run(tray);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "Simple Video Snipper", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private sealed class RecordingTray : ApplicationContext
    {
        private readonly RecordingHost host;
        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly Form window;
        private readonly System.Windows.Forms.Timer refresh;
        private readonly GlobalCaptureShortcut shortcut;
        private readonly SaveWorkflow saves;
        private RecordingToolbar? captureToolbar;
        private bool selecting;

        public RecordingTray(RecordingHost host, PreferencesStore preferences, bool openOnLaunch)
        {
            this.host = host;
            menu = new ContextMenuStrip();
            icon = new NotifyIcon
            {
                Text = "Simple Video Snipper",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };
            saves = new SaveWorkflow(host.Folder, preferences, icon);
            var selectArea = menu.Items.Add("Record area", null, (_, _) => BeginCapture(windowMode: false));
            var selectWindow = menu.Items.Add("Record window", null, (_, _) => BeginCapture(windowMode: true));
            menu.Items.Add("Open Chrome recorder", null, (_, _) => OpenRecorder());
            var stop = menu.Items.Add("Stop recording", null, (_, _) => host.RequestStop());
            var settings = menu.Items.Add("Save settings…", null, (_, _) => saves.ShowSettings());
            var pending = menu.Items.Add("Unsaved recordings…", null, (_, _) => saves.ShowPending());
            menu.Items.Add("Open save folder", null, (_, _) => saves.OpenSaveFolder());
            menu.Items.Add(new ToolStripSeparator());
            var exit = menu.Items.Add("Exit", null, (_, _) => ExitThread());
            menu.Opening += (_, _) =>
            {
                selectArea.Enabled = !selecting && !host.IsRecording && !saves.Busy;
                selectWindow.Enabled = selectArea.Enabled;
                stop.Enabled = host.IsRecording;
                settings.Enabled = pending.Enabled = !selecting && !host.IsRecording && !saves.Busy;
                exit.Enabled = !selecting && !host.IsRecording && !saves.Busy;
            };

            var areaButton = new Button { Text = "Record area", AutoSize = true };
            areaButton.Click += (_, _) => BeginCapture(windowMode: false);
            var windowButton = new Button { Text = "Record window", AutoSize = true };
            windowButton.Click += (_, _) => BeginCapture(windowMode: true);
            var openButton = new Button { Text = "Open Chrome recorder", AutoSize = true };
            openButton.Click += (_, _) => OpenRecorder();
            var stopButton = new Button { Text = "Stop recording", AutoSize = true, Enabled = false };
            stopButton.Click += (_, _) => host.RequestStop();
            var settingsButton = new Button { Text = "Settings", AutoSize = true };
            settingsButton.Click += (_, _) => saves.ShowSettings();
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
            };
            shortcut = new GlobalCaptureShortcut(() => BeginCapture(windowMode: false));
            var shortcutLabel = new Label
            {
                Text = shortcut.IsRegistered ? "Area shortcut: Ctrl+Alt+R" : "Ctrl+Alt+R is unavailable. Use Record area.",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 3),
            };
            actions.Controls.AddRange([areaButton, windowButton, openButton, stopButton, settingsButton, shortcutLabel]);
            actions.SetFlowBreak(settingsButton, true);
            if (shortcut.IsRegistered)
                selectArea.Text = "Record area (Ctrl+Alt+R)";

            window = new Form
            {
                Text = "Simple Video Snipper",
                ClientSize = new Size(615, 90),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                AutoScaleMode = AutoScaleMode.Dpi,
                AcceptButton = openButton,
            };
            icon.DoubleClick += (_, _) =>
            {
                if (captureToolbar is not null)
                {
                    captureToolbar.Activate();
                    return;
                }
                window.Show();
                window.Activate();
            };
            window.Controls.Add(actions);
            window.FormClosing += (_, args) =>
            {
                if (args.CloseReason == CloseReason.UserClosing)
                {
                    args.Cancel = true;
                    window.Hide();
                }
            };
            refresh = new System.Windows.Forms.Timer { Interval = 250 };
            refresh.Tick += (_, _) =>
            {
                var recording = host.IsRecording;
                stopButton.Enabled = recording;
                areaButton.Enabled = !recording && !saves.Busy;
                windowButton.Enabled = areaButton.Enabled;
                settingsButton.Enabled = !recording && !selecting && !saves.Busy;
                if (!recording && !selecting)
                    saves.ProcessReady();
            };
            refresh.Start();
            host.RecordingReady += OnRecordingReady;
            window.Shown += (_, _) => saves.Start();
            if (openOnLaunch)
                window.Shown += (_, _) => OpenRecorder();
            window.Show();
        }

        private void OnRecordingReady(PendingClip clip)
        {
            try
            {
                if (window.IsHandleCreated && !window.IsDisposed)
                    window.BeginInvoke(() => saves.Enqueue(clip));
            }
            catch (InvalidOperationException) when (!window.IsHandleCreated || window.IsDisposed)
            {
                // The finalized clip will be available in Unsaved recordings on the next launch.
            }
        }

        private void OpenRecorder() => Open("chrome.exe", host.RecorderUrl);

        private async void BeginCapture(bool windowMode)
        {
            if (captureToolbar is not null)
            {
                captureToolbar.Activate();
                return;
            }
            if (selecting || saves.Busy)
                return;
            if (host.IsRecording)
            {
                window.Show();
                window.Activate();
                return;
            }

            var showWindowOnCancel = window.Visible;
            string? staged = null;
            window.Hide();
            try
            {
                selecting = true;
                CaptureTarget target;
                AudioSelection audio;
                try
                {
                    var selection = windowMode ? ChooseWindow() : ChooseArea();
                    using var options = selection is null ? null : new AudioOptionsDialog();
                    if (selection is null || options!.ShowDialog() != DialogResult.OK)
                    {
                        if (showWindowOnCancel)
                            window.Show();
                        return;
                    }

                    target = selection.Value;
                    audio = options.Selection;
                }
                finally
                {
                    selecting = false;
                }

                using var session = host.TryStartRecording();
                if (session is null)
                {
                    ShowError("Finish the current recording before starting another.");
                    return;
                }

                using var toolbar = new RecordingToolbar(target, session);
                captureToolbar = toolbar;
                var excluded = NativeCaptureMethods.ExcludeWindow(toolbar);
                toolbar.Show();
                if (!target.IsWindow && !excluded && toolbar.Bounds.IntersectsWith(target.Bounds))
                    toolbar.Hide(); // The tray Stop remains available for full-display selections.

                var warnings = new Progress<string>(message =>
                {
                    if (!toolbar.IsDisposed)
                        toolbar.ShowAudioWarning(message);
                    icon.ShowBalloonTip(4000, "Recording audio", message, ToolTipIcon.Warning);
                });
                staged = await Task.Run(() => ScreenRecorder.RecordAsync(target, audio, host.Folder,
                    session.StopRequested, warnings));
            }
            catch (Exception exception)
            {
                ShowError(exception.GetBaseException().Message);
            }
            finally
            {
                captureToolbar = null;
            }
            if (staged is not null)
                saves.Enqueue(new PendingClip(staged, ExportFormat.Mp4));
        }

        private static CaptureTarget? ChooseArea()
        {
            using var overlay = new AreaSelectionOverlay(Screen.FromPoint(Cursor.Position));
            NativeCaptureMethods.ExcludeWindow(overlay);
            return overlay.ShowDialog() == DialogResult.OK && !overlay.Selection.Region.IsEmpty
                ? CaptureTarget.FromArea(overlay.Selection)
                : null;
        }

        private static CaptureTarget? ChooseWindow()
        {
            using var picker = new WindowSelectionDialog();
            return picker.ShowDialog() == DialogResult.OK && picker.Selection is { } selected
                ? new CaptureTarget(selected.Handle, selected.Bounds, null)
                : null;
        }

        private static void Open(string target, string arguments = "")
        {
            try
            {
                Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true });
            }
            catch (Win32Exception exception)
            {
                ShowError(exception.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
                host.RecordingReady -= OnRecordingReady;
                shortcut.Dispose();
                window.Dispose();
                saves.Dispose();
                icon.Dispose();
                menu.Dispose();
                host.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
