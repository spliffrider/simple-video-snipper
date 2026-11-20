namespace SimpleVideoSnipper;

internal sealed class ClipEditor : Form
{
    private readonly ClipEditMedia media;
    private readonly CropPreview preview;
    private readonly TrimTimeline timeline;
    private readonly Label position = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
    private readonly Label range = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label previewStatus = new() { AutoSize = true, Text = "Loading preview…" };
    private readonly Button play = new() { Text = "Play", MinimumSize = new Size(65, 0), AutoSize = true };
    private readonly NumericUpDown previewVolume = new() { Value = 65, Width = 55, AccessibleName = "Preview volume percentage" };
    private readonly CheckBox mute = new() { Text = "Mute original audio", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Button setStart = new() { Text = "Set start", AutoSize = true };
    private readonly Button setEnd = new() { Text = "Set end", AutoSize = true };
    private readonly Button resetTrim = new() { Text = "Reset trim", AutoSize = true };
    private readonly Button resetCrop = new() { Text = "Reset crop", AutoSize = true };
    private readonly Label musicName = new() { AutoSize = true, Margin = new Padding(4, 7, 8, 0), MaximumSize = new Size(240, 32) };
    private readonly Label musicLevel = new() { Text = "Volume (%)", AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
    private readonly NumericUpDown volume = new() { Value = 25, Width = 55, AccessibleName = "Music volume percentage" };
    private readonly Button removeMusic = new() { Text = "Remove", AutoSize = true };
    private readonly Panel canvas = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer scrubDelay = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer playbackClock = new() { Interval = 75 };
    private CancellationTokenSource? previewRequest;
    private CancellationTokenSource? playbackRequest;
    private Task previewTask = Task.CompletedTask;
    private Task playbackTask = Task.CompletedTask;
    private ClipPlayback? playback;
    private bool stopping;
    private bool playRequested;
    private int playingStart;
    private int playingEnd;
    private string? musicPath;

    public ClipEditor(string clipTitle, ClipEditMedia media, Func<IWin32Window, string, Task> saveGif)
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
        canvas.Controls.Add(preview);
        timeline = new TrimTimeline(media.Duration);
        mute.Visible = media.HasAudio;

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var back = new Button { Text = "Back", AutoSize = true, DialogResult = DialogResult.Cancel };
        var exportGif = new Button { Text = "Export GIF…", AutoSize = true };
        AcceptButton = save;
        CancelButton = back;

        var description = new Label
        {
            Text = "Drag the handles to trim. Click the timeline to scrub. Pause to draw a crop on the picture.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 10),
        };
        var timelineRow = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0),
        };
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timelineRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timelineRow.Controls.Add(position, 0, 0);
        timelineRow.Controls.Add(previewStatus, 1, 0);
        timelineRow.Controls.Add(timeline, 0, 1);
        timelineRow.SetColumnSpan(timeline, 2);
        var rewind = new Button { Text = "Back 5s", AutoSize = true, AccessibleName = "Go back five seconds" };
        var forward = new Button { Text = "Forward 5s", AutoSize = true, AccessibleName = "Go forward five seconds" };
        var transport = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0),
        };
        transport.Controls.AddRange([rewind, play, forward,
            new Label { Text = "Preview volume (%)", AutoSize = true, Margin = new Padding(12, 8, 3, 0) }, previewVolume]);
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
        actions.Controls.AddRange([back, exportGif, save]);
        footer.Controls.Add(actions, 1, 0);

        var layout = new TableLayoutPanel { RowCount = 8, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var row = 2; row < layout.RowCount; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(canvas, 0, 1);
        layout.Controls.Add(timelineRow, 0, 2);
        layout.Controls.Add(transport, 0, 3);
        layout.Controls.Add(tools, 0, 4);
        layout.Controls.Add(range, 0, 5);
        layout.Controls.Add(music, 0, 6);
        layout.Controls.Add(footer, 0, 7);
        Controls.Add(layout);

        setStart.Click += (_, _) => timeline.SetStartAtPlayhead();
        setEnd.Click += (_, _) => timeline.SetEndAtPlayhead();
        resetTrim.Click += (_, _) => timeline.ResetTrim();
        resetCrop.Click += (_, _) => preview.ResetCrop();
        addMusic.Click += (_, _) => ChooseMusic();
        removeMusic.Click += (_, _) => SetMusic(null);
        exportGif.Click += (_, _) =>
        {
            PausePlayback(syncPosition: true);
            using var gif = new GifPreviewDialog(media, Edits, saveGif);
            gif.ShowDialog(this);
        };
        play.Click += (_, _) => TogglePlayback();
        rewind.Click += (_, _) => Skip(-5);
        forward.Click += (_, _) => Skip(5);
        previewVolume.ValueChanged += (_, _) => playback?.SetVolume((double)previewVolume.Value / 100);
        mute.CheckedChanged += (_, _) => PausePlayback(syncPosition: true);
        volume.ValueChanged += (_, _) => PausePlayback(syncPosition: true);
        preview.CropChanged += (_, _) => UpdateLabels();
        timeline.Changed += (_, _) => TimelineChanged();
        scrubDelay.Tick += (_, _) => StartPreview();
        playbackClock.Tick += (_, _) => SyncPlayhead();
        Shown += (_, _) => StartPreview();
        SetMusic(null);
        UpdateLabels();
    }

    public ClipEdits? Edits => !timeline.IsTrimmed && preview.Crop is null && !mute.Checked && musicPath is null
        ? null : new ClipEdits(TimeAt(timeline.Start), timeline.End == timeline.Maximum ? null : TimeAt(timeline.End),
            preview.Crop, media.VideoSize, mute.Checked)
        {
            HasOriginalAudio = media.HasAudio,
            Music = musicPath is null ? null : new MusicTrack(musicPath, (int)volume.Value),
        };

    private void ChooseMusic()
    {
        PausePlayback(syncPosition: true);
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
        PausePlayback(syncPosition: true);
        musicPath = path;
        var selected = path is not null;
        musicName.Text = selected ? Path.GetFileName(path) : "";
        musicName.Visible = musicLevel.Visible = volume.Visible = removeMusic.Visible = selected;
    }

    private TimeSpan TimeAt(int value) => TimeSpan.FromTicks((long)Math.Round(media.Duration.Ticks *
        (value / (double)timeline.Maximum)));

    private int TimelinePosition(TimeSpan time) => (int)Math.Round(timeline.Maximum * (time.Ticks / (double)media.Duration.Ticks));

    private void UpdateLabels()
    {
        var current = TimeAt(timeline.Value);
        position.Text = $"{Clock(current)} / {Clock(media.Duration)}";
        var trimStart = TimeAt(timeline.Start);
        var trimEnd = TimeAt(timeline.End);
        var selectedLength = trimEnd - trimStart;
        setStart.Enabled = timeline.CanSetStart;
        setEnd.Enabled = timeline.CanSetEnd;
        resetTrim.Enabled = timeline.IsTrimmed;
        resetCrop.Enabled = preview.Crop is not null;
        var crop = preview.Crop is { } selection ? $"   •   Crop {selection.Width} × {selection.Height}" : "";
        range.Text = $"Keep {Clock(trimStart)} to {Clock(trimEnd)} ({Clock(selectedLength)}){crop}";
    }

    internal static string Clock(TimeSpan time) => time.TotalHours >= 1
        ? $"{(long)time.TotalHours}:{time:mm\\:ss\\.ff}" : time.ToString(@"mm\:ss\.ff");

    private void StartPreview()
    {
        if (stopping || playRequested || !previewTask.IsCompleted)
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
            if (stopping || playRequested || request.IsCancellationRequested)
                return;
            previewStatus.Text = image is null ? "No frame at this position" : "";
            if (image is not null)
                preview.SetFrame(new Bitmap(image));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            if (!stopping && !playRequested && !request.IsCancellationRequested)
                previewStatus.Text = "Preview unavailable";
        }
        finally
        {
            previewRequest = null;
        }
    }

    private void TimelineChanged()
    {
        UpdateLabels();
        if (!playRequested)
        {
            QueueFrame();
            return;
        }

        if (timeline.Start != playingStart || timeline.End != playingEnd ||
            timeline.Value < playingStart || timeline.Value >= playingEnd)
        {
            PausePlayback(syncPosition: false);
            return;
        }
        playback?.Seek(TimeAt(timeline.Value) - TimeAt(playingStart));
    }

    private void QueueFrame()
    {
        previewStatus.Text = "Seeking…";
        previewRequest?.Cancel();
        scrubDelay.Stop();
        scrubDelay.Start();
    }

    private void TogglePlayback()
    {
        if (stopping)
            return;
        if (playRequested)
        {
            PausePlayback(syncPosition: true);
            return;
        }
        if (playbackRequest is not null)
            return;

        playingStart = timeline.Start;
        playingEnd = timeline.End;
        if (timeline.Value < playingStart || timeline.Value >= playingEnd)
            timeline.ShowPlaybackPosition(playingStart);
        UpdateLabels();

        playRequested = true;
        play.Text = "Pause";
        scrubDelay.Stop();
        previewRequest?.Cancel();
        previewStatus.Text = "Preparing playback…";
        playbackTask = OpenPlaybackAsync(Edits);
    }

    private async Task OpenPlaybackAsync(ClipEdits? edits)
    {
        using var request = new CancellationTokenSource();
        playbackRequest = request;
        try
        {
            var source = await media.PlaybackSourceAsync(edits, request.Token);
            if (!playRequested || stopping || request.IsCancellationRequested)
                return;

            playback ??= CreatePlayback();
            previewStatus.Text = "Opening playback…";
            playback.SetVolume((double)previewVolume.Value / 100);
            playback.Show();
            playback.BringToFront();
            playback.Open(source.Path, source.Duration, TimeAt(timeline.Value) - TimeAt(playingStart));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (playRequested && !stopping && !request.IsCancellationRequested)
                PlaybackFailed(exception.Message);
        }
        finally
        {
            playbackRequest = null;
            if (!stopping)
                play.Enabled = true;
        }
    }

    private ClipPlayback CreatePlayback()
    {
        var player = new ClipPlayback { Visible = false };
        player.Opened += () =>
        {
            previewStatus.Text = "";
            playbackClock.Start();
        };
        player.Ended += () =>
        {
            if (!playRequested)
                return;
            timeline.ShowPlaybackPosition(playingEnd);
            UpdateLabels();
            PausePlayback(syncPosition: false);
        };
        player.PauseRequested += () => PausePlayback(syncPosition: true);
        player.Failed += PlaybackFailed;
        canvas.Controls.Add(player);
        return player;
    }

    private void PlaybackFailed(string message)
    {
        PausePlayback(syncPosition: false);
        MessageBox.Show(this, message, "Could not play preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void PausePlayback(bool syncPosition)
    {
        if (!playRequested)
            return;
        if (syncPosition)
            SyncPlayhead();
        playRequested = false;
        playbackClock.Stop();
        playbackRequest?.Cancel();
        playback?.CloseMedia();
        playback?.Hide();
        play.Text = "Play";
        play.Enabled = playbackRequest is null;
        QueueFrame();
    }

    private void SyncPlayhead()
    {
        if (!playRequested || playback?.IsOpen != true)
            return;
        timeline.ShowPlaybackPosition(Math.Clamp(TimelinePosition(TimeAt(playingStart) + playback.Position), playingStart, playingEnd));
        UpdateLabels();
    }

    private void Skip(int seconds)
    {
        SyncPlayhead();
        var target = Math.Clamp(TimeAt(timeline.Value).TotalSeconds + seconds,
            TimeAt(timeline.Start).TotalSeconds, TimeAt(timeline.End).TotalSeconds);
        var next = TimelinePosition(TimeSpan.FromSeconds(target));
        if (next == timeline.Value && next == timeline.End)
            PausePlayback(syncPosition: false);
        else
            timeline.Scrub(next);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Space)
        {
            if ((message.LParam.ToInt64() & (1L << 30)) == 0)
                TogglePlayback();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    public Task StopPreviewAsync()
    {
        stopping = true;
        scrubDelay.Stop();
        playbackClock.Stop();
        previewRequest?.Cancel();
        playbackRequest?.Cancel();
        playback?.CloseMedia();
        return Task.WhenAll(previewTask, playbackTask);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            scrubDelay.Dispose();
            playbackClock.Dispose();
        }
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
