namespace SimpleVideoSnipper;

internal sealed class ClipEditor : Form
{
    private readonly ClipEditMedia media;
    private readonly CropPreview preview;
    private readonly TrackBar timeline = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, Height = 35, TickStyle = TickStyle.None, AccessibleName = "Scrub video",
    };
    private readonly Label position = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label range = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label previewStatus = new() { AutoSize = true, Text = "Loading preview…" };
    private readonly Button playPreview = new() { Text = "Play preview", AutoSize = true };
    private readonly Button save = new() { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly CheckBox mute = new() { Text = "Mute original audio", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button setStart = new() { Text = "Set start", AutoSize = true };
    private readonly Button setEnd = new() { Text = "Set end", AutoSize = true };
    private readonly Button resetTrim = new() { Text = "Reset trim", AutoSize = true };
    private readonly Button resetCrop = new() { Text = "Reset crop", AutoSize = true };
    private readonly Label musicName = new() { AutoSize = true, Margin = new Padding(4, 7, 8, 0), MaximumSize = new Size(240, 32) };
    private readonly Label musicLevel = new() { Text = "Volume (%)", AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
    private readonly NumericUpDown volume = new() { Value = 25, Width = 55, AccessibleName = "Music volume percentage" };
    private readonly Button removeMusic = new() { Text = "Remove", AutoSize = true };
    private readonly TableLayoutPanel layout = new() { RowCount = 7, ColumnCount = 1, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer scrubDelay = new() { Interval = 100 };
    private CancellationTokenSource? previewRequest;
    private CancellationTokenSource? playbackRequest;
    private Task previewTask = Task.CompletedTask;
    private Task playbackTask = Task.CompletedTask;
    private ClipPlayback? playback;
    private bool stopping;
    private bool reviewing;
    private int start;
    private int end;
    private string? musicPath;

    public ClipEditor(string clipTitle, ClipEditMedia media)
    {
        this.media = media;
        Text = $"Edit recording — {clipTitle}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 630);
        MinimumSize = new Size(610, 470);
        MinimizeBox = false;
        Padding = new Padding(16);

        preview = new CropPreview(media.VideoSize) { Dock = DockStyle.Fill, AccessibleName = "Video crop preview" };
        timeline.Maximum = (int)Math.Clamp(Math.Ceiling(media.Duration.TotalMilliseconds), 1, 1_000_000);
        timeline.SmallChange = Math.Max(1, timeline.Maximum / 300);
        timeline.LargeChange = Math.Max(1, timeline.Maximum / 30);
        end = timeline.Maximum;
        mute.Visible = media.HasAudio;

        var back = new Button { Text = "Back", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = save;
        CancelButton = back;

        var description = new Label
        {
            Text = "Drag on the preview to crop. Scrub to a frame to set the start or end.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 10),
        };
        var timelineRow = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0),
        };
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timelineRow.Controls.Add(position, 0, 0);
        timelineRow.Controls.Add(previewStatus, 1, 0);
        timelineRow.Controls.Add(playPreview, 2, 0);
        timelineRow.Controls.Add(timeline, 0, 1);
        timelineRow.SetColumnSpan(timeline, 3);
        var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        tools.Controls.AddRange([setStart, setEnd, resetTrim, resetCrop]);

        var addMusic = new Button { Text = "Add music…", AutoSize = true };
        var music = new TableLayoutPanel
        {
            ColumnCount = 5, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0),
        };
        for (var column = 0; column < music.ColumnCount; column++)
            music.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        music.Controls.Add(addMusic, 0, 0);
        music.Controls.Add(musicName, 1, 0);
        music.Controls.Add(musicLevel, 2, 0);
        music.Controls.Add(volume, 3, 0);
        music.Controls.Add(removeMusic, 4, 0);

        var footer = new TableLayoutPanel
        {
            ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(mute, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        actions.Controls.AddRange([back, save]);
        footer.Controls.Add(actions, 1, 0);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var row = 2; row < layout.RowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(preview, 0, 1);
        layout.Controls.Add(timelineRow, 0, 2);
        layout.Controls.Add(tools, 0, 3);
        layout.Controls.Add(range, 0, 4);
        layout.Controls.Add(music, 0, 5);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(layout);

        setStart.Click += (_, _) => { start = timeline.Value; UpdateLabels(); };
        setEnd.Click += (_, _) => { end = timeline.Value; UpdateLabels(); };
        resetTrim.Click += (_, _) => { start = 0; end = timeline.Maximum; UpdateLabels(); };
        resetCrop.Click += (_, _) => preview.ResetCrop();
        addMusic.Click += (_, _) => ChooseMusic();
        removeMusic.Click += (_, _) => SetMusic(null);
        playPreview.Click += (_, _) => BeginPlayback();
        preview.CropChanged += (_, _) => UpdateLabels();
        timeline.ValueChanged += (_, _) =>
        {
            UpdateLabels();
            previewStatus.Text = "Seeking…";
            previewRequest?.Cancel();
            scrubDelay.Stop();
            scrubDelay.Start();
        };
        scrubDelay.Tick += (_, _) => StartPreview();
        Shown += (_, _) => StartPreview();
        SetMusic(null);
        UpdateLabels();
    }

    public ClipEdits? Edits => start == 0 && end == timeline.Maximum && preview.Crop is null && !mute.Checked && musicPath is null
        ? null : new ClipEdits(TimeAt(start), end == timeline.Maximum ? null : TimeAt(end), preview.Crop, media.VideoSize, mute.Checked)
        {
            HasOriginalAudio = media.HasAudio,
            Music = musicPath is null ? null : new MusicTrack(musicPath, (int)volume.Value),
        };

    private void ChooseMusic()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose music for this clip",
            Filter = "Audio files (*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac)|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac",
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            SetMusic(picker.FileName);
    }

    private void SetMusic(string? path)
    {
        musicPath = path;
        var selected = path is not null;
        musicName.Text = selected ? Path.GetFileName(path) : "";
        musicName.Visible = musicLevel.Visible = volume.Visible = removeMusic.Visible = selected;
    }

    private TimeSpan TimeAt(int value) => TimeSpan.FromTicks((long)Math.Round(media.Duration.Ticks *
        (value / (double)timeline.Maximum)));

    private void UpdateLabels()
    {
        var current = TimeAt(timeline.Value);
        position.Text = $"{Clock(current)} / {Clock(media.Duration)}";
        var trimStart = TimeAt(start);
        var trimEnd = TimeAt(end);
        var selectedLength = trimEnd - trimStart;
        var minimum = TimeSpan.FromMilliseconds(Math.Min(100, media.Duration.TotalMilliseconds));
        setStart.Enabled = trimEnd - current >= minimum;
        setEnd.Enabled = current - trimStart >= minimum;
        resetTrim.Enabled = start != 0 || end != timeline.Maximum;
        resetCrop.Enabled = preview.Crop is not null;
        var crop = preview.Crop is { } selection ? $"   •   Crop {selection.Width} × {selection.Height}" : "";
        range.Text = $"Keep {Clock(trimStart)} to {Clock(trimEnd)} ({Clock(selectedLength)}){crop}";
    }

    internal static string Clock(TimeSpan time) => time.TotalHours >= 1
        ? $"{(long)time.TotalHours}:{time:mm\\:ss\\.ff}" : time.ToString(@"mm\:ss\.ff");

    private void StartPreview()
    {
        if (stopping || reviewing || !previewTask.IsCompleted)
            return;
        scrubDelay.Stop();
        previewTask = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        using var request = new CancellationTokenSource();
        previewRequest = request;
        try
        {
            var lastFramePosition = media.Duration - TimeSpan.FromMilliseconds(100);
            var requested = TimeAt(timeline.Value);
            var seek = requested < lastFramePosition ? requested : lastFramePosition;
            if (seek < TimeSpan.Zero)
                seek = TimeSpan.Zero;

            using var image = await media.FrameAsync(seek, request.Token);
            if (stopping || reviewing || request.IsCancellationRequested)
                return;
            previewStatus.Text = image is null ? "No frame at this position" : "";
            if (image is not null)
                preview.SetFrame(new Bitmap(image));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            if (!stopping)
                previewStatus.Text = "Preview unavailable";
        }
        finally
        {
            previewRequest = null;
        }
    }

    private void BeginPlayback()
    {
        if (stopping || reviewing || !playbackTask.IsCompleted)
            return;
        if (playback is null)
        {
            playback = new ClipPlayback();
            playback.BackRequested += (_, _) => ReturnToEdits();
            playback.SaveRequested += (_, _) => DialogResult = DialogResult.OK;
            Controls.Add(playback);
        }

        // Read the controls before hiding the editor.
        var edits = Edits;
        reviewing = true;
        playPreview.Enabled = false;
        scrubDelay.Stop();
        previewRequest?.Cancel();
        layout.Hide();
        AcceptButton = null;
        playback.Prepare();
        playback.Show();
        playback.BringToFront();
        playbackTask = OpenPlaybackAsync(playback, edits);
    }

    private async Task OpenPlaybackAsync(ClipPlayback panel, ClipEdits? edits)
    {
        using var request = new CancellationTokenSource();
        playbackRequest = request;
        try
        {
            var source = await media.PlaybackSourceAsync(edits, request.Token);
            if (reviewing && !stopping && !request.IsCancellationRequested)
                panel.Open(source.Path, source.Duration);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (reviewing && !stopping && !request.IsCancellationRequested)
                panel.Fail(exception.Message);
        }
        finally
        {
            playbackRequest = null;
            if (!stopping && !reviewing)
                playPreview.Enabled = true;
        }
    }

    private void ReturnToEdits()
    {
        reviewing = false;
        playbackRequest?.Cancel();
        playback?.CloseMedia();
        playback?.Hide();
        layout.Show();
        AcceptButton = save;
        playPreview.Enabled = playbackTask.IsCompleted;
        playPreview.Focus();
        scrubDelay.Start();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (reviewing && keyData == Keys.Escape)
        {
            ReturnToEdits();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    public Task StopPreviewAsync()
    {
        stopping = true;
        scrubDelay.Stop();
        previewRequest?.Cancel();
        playbackRequest?.Cancel();
        playback?.CloseMedia();
        return Task.WhenAll(previewTask, playbackTask);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            scrubDelay.Dispose();
        base.Dispose(disposing);
    }

    private sealed class CropPreview : Control
    {
        private readonly Size videoSize;
        private Bitmap? frame;
        private Point? dragStart;
        private Point dragEnd;

        public CropPreview(Size videoSize)
        {
            this.videoSize = videoSize;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(17, 23, 32);
        }

        public Rectangle? Crop { get; private set; }
        public event EventHandler? CropChanged;

        public void SetFrame(Bitmap next)
        {
            frame?.Dispose();
            frame = next;
            Cursor = Cursors.Cross;
            Invalidate();
        }

        public void ResetCrop() => SetCrop(null);

        private void SetCrop(Rectangle? crop)
        {
            Crop = crop;
            Invalidate();
            CropChanged?.Invoke(this, EventArgs.Empty);
        }

        private Rectangle VideoBounds()
        {
            var scale = Math.Min(ClientSize.Width / (double)videoSize.Width, ClientSize.Height / (double)videoSize.Height);
            var width = (int)Math.Round(videoSize.Width * scale);
            var height = (int)Math.Round(videoSize.Height * scale);
            return new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        }

        private Point VideoPoint(Point client, Rectangle bounds) => new(
            (int)Math.Round(Math.Clamp((client.X - bounds.X) / (double)bounds.Width, 0, 1) * videoSize.Width),
            (int)Math.Round(Math.Clamp((client.Y - bounds.Y) / (double)bounds.Height, 0, 1) * videoSize.Height));

        private Rectangle CropBetween(Point first, Point second)
        {
            var rect = Rectangle.FromLTRB(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
                Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
            // Even crop dimensions are compatible with both export encoders.
            return new Rectangle(rect.Location, new Size(rect.Width & ~1, rect.Height & ~1));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var bounds = VideoBounds();
            if (e.Button != MouseButtons.Left || frame is null || !bounds.Contains(e.Location))
                return;
            dragStart = dragEnd = VideoPoint(e.Location, bounds);
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var bounds = VideoBounds();
            if (dragStart is null || bounds.Width <= 0 || bounds.Height <= 0)
                return;
            dragEnd = VideoPoint(e.Location, bounds);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var bounds = VideoBounds();
            if (e.Button != MouseButtons.Left || dragStart is not { } start || bounds.Width <= 0 || bounds.Height <= 0)
                return;
            dragStart = null;
            Capture = false;
            var rect = CropBetween(start, VideoPoint(e.Location, bounds));
            if (rect.Width >= 2 && rect.Height >= 2)
                SetCrop(rect.Size == videoSize ? null : rect);
            else
                Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && dragStart is not null)
            {
                dragStart = null;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bounds = VideoBounds();
            if (frame is null || bounds.Width <= 0 || bounds.Height <= 0)
                return;
            e.Graphics.DrawImage(frame, bounds);
            var selection = dragStart is { } start ? CropBetween(start, dragEnd) : Crop;
            if (selection is not { Width: > 0, Height: > 0 } crop)
                return;
            var target = Rectangle.FromLTRB(
                bounds.X + (int)Math.Round(crop.Left * (double)bounds.Width / videoSize.Width),
                bounds.Y + (int)Math.Round(crop.Top * (double)bounds.Height / videoSize.Height),
                bounds.X + (int)Math.Round(crop.Right * (double)bounds.Width / videoSize.Width),
                bounds.Y + (int)Math.Round(crop.Bottom * (double)bounds.Height / videoSize.Height));
            using var shade = new SolidBrush(Color.FromArgb(155, 11, 15, 20));
            using var outside = new Region(bounds);
            outside.Exclude(target);
            e.Graphics.FillRegion(shade, outside);
            using var outline = new Pen(Color.FromArgb(111, 226, 255), 2);
            e.Graphics.DrawRectangle(outline, target);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                frame?.Dispose();
            base.Dispose(disposing);
        }
    }
}
