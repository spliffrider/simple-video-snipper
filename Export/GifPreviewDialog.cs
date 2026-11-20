namespace SimpleVideoSnipper;

internal sealed class GifPreviewDialog : Form
{
    private readonly ClipEditMedia media;
    private readonly ClipEdits? edits;
    private readonly Func<IWin32Window, string, Task> saveGif;
    private readonly GifAnimationView preview = new() { Dock = DockStyle.Fill, AccessibleName = "Animated GIF preview" };
    private readonly ComboBox preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95, AccessibleName = "GIF preset" };
    private readonly CheckBox hold = new() { Text = "Hold last frame for 1 second", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    private readonly ComboBox zoom = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105, AccessibleName = "Preview zoom" };
    private readonly Button play = new() { Text = "Pause", AutoSize = true, MinimumSize = new Size(65, 0), Enabled = false };
    private readonly Button render = new() { Text = "Cancel render", AutoSize = true };
    private readonly Button save = new() { Text = "Save GIF…", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 4), MaximumSize = new Size(520, 0) };
    private CancellationTokenSource? renderRequest;
    private Task renderTask = Task.CompletedTask;
    private string? previewPath;
    private bool closing;
    private bool saving;

    public GifPreviewDialog(ClipEditMedia media, ClipEdits? edits, Func<IWin32Window, string, Task> saveGif)
    {
        this.media = media;
        this.edits = edits;
        this.saveGif = saveGif;
        Text = "Preview GIF";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 490);
        MinimumSize = new Size(510, 300);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(12);

        preset.Items.AddRange(["Small", "Clear"]);
        zoom.Items.AddRange(["Fit", "Actual size"]);
        preset.SelectedIndex = 0;
        zoom.SelectedIndex = 0;
        var back = new Button { Text = "Back", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = save;
        CancelButton = back;

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        options.Controls.AddRange([new Label { Text = "Preset", AutoSize = true, Margin = new Padding(0, 7, 4, 0) },
            preset, hold]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        actions.Controls.AddRange([play, zoom, render, save, back]);
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Dock = DockStyle.Fill };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(options, 0, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);

        Shown += (_, _) => RequestRender();
        preset.SelectedIndexChanged += (_, _) => RequestRender();
        hold.CheckedChanged += (_, _) => RequestRender();
        zoom.SelectedIndexChanged += (_, _) => preview.SetActualSize(zoom.SelectedIndex == 1);
        play.Click += (_, _) =>
        {
            preview.SetPlaying(!preview.IsPlaying);
            play.Text = preview.IsPlaying ? "Pause" : "Play";
        };
        render.Click += (_, _) =>
        {
            if (renderRequest is { } request)
            {
                status.Text = "Cancelling preview…";
                render.Enabled = false;
                request.Cancel();
            }
            else
                RequestRender();
        };
        save.Click += async (_, _) => await SaveAsync();
    }

    private void RequestRender()
    {
        if (closing)
            return;

        renderRequest?.Cancel();
        RemovePreview();
        save.Enabled = play.Enabled = false;
        render.Text = "Cancel render";
        render.Visible = render.Enabled = true;
        status.Text = $"Rendering {preset.Text} preview…";

        var request = new CancellationTokenSource();
        renderRequest = request;
        var clear = preset.SelectedIndex == 1;
        renderTask = RenderAsync(renderTask, clear ? 720 : 480, clear ? 12 : 10, hold.Checked, request);
    }

    private async Task RenderAsync(Task previous, int longestSide, int fps, bool holdLastFrame, CancellationTokenSource request)
    {
        using (request)
        {
            string? output = null;
            try
            {
                await previous;
                request.Token.ThrowIfCancellationRequested();
                output = await media.RenderGifAsync(edits, longestSide, fps, holdLastFrame, request.Token);
                request.Token.ThrowIfCancellationRequested();
                var length = new FileInfo(output).Length;
                preview.Open(output);
                previewPath = output;
                output = null;
                var size = length >= 1_000_000 ? $"{length / 1_000_000d:0.##} MB" : $"{length / 1_000d:0.##} KB";
                status.Text = $"{preview.ImageSize.Width} × {preview.ImageSize.Height} px  •  {fps} fps  •  {size}";
                play.Enabled = preview.IsPlaying;
                play.Text = preview.IsPlaying ? "Pause" : "Play";
                save.Enabled = true;
                render.Visible = false;
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                if (ReferenceEquals(renderRequest, request) && !closing)
                    status.Text = "Preview cancelled.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
            {
                if (ReferenceEquals(renderRequest, request) && !closing)
                    status.Text = $"Could not render preview. {exception.Message}";
            }
            finally
            {
                if (output is not null)
                    RecordingExporter.TryDelete(output);
                if (ReferenceEquals(renderRequest, request))
                {
                    renderRequest = null;
                    render.Text = "Render again";
                    render.Enabled = !closing;
                }
            }
        }
    }

    private async Task SaveAsync()
    {
        if (saving || closing || previewPath is not { } path)
            return;

        saving = true;
        save.Enabled = preset.Enabled = hold.Enabled = false;
        try
        {
            await saveGif(this, path);
        }
        finally
        {
            saving = false;
            save.Enabled = preset.Enabled = hold.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
            return;
        if (saving)
        {
            e.Cancel = true;
            return;
        }
        if (!renderTask.IsCompleted)
        {
            e.Cancel = true;
            if (!closing)
            {
                closing = true;
                status.Text = "Cancelling preview…";
                render.Enabled = preset.Enabled = hold.Enabled = false;
                renderRequest?.Cancel();
                _ = CloseAfterRenderAsync();
            }
        }
    }

    private async Task CloseAfterRenderAsync()
    {
        await renderTask;
        Close();
    }

    private void RemovePreview()
    {
        preview.Clear();
        if (previewPath is { } path)
        {
            RecordingExporter.TryDelete(path);
            previewPath = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            RemovePreview();
        base.Dispose(disposing);
    }
}
