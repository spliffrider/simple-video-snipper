using System.Windows.Forms.Integration;
using MediaElement = System.Windows.Controls.MediaElement;
using MediaState = System.Windows.Controls.MediaState;
using Stretch = System.Windows.Media.Stretch;

namespace SimpleVideoSnipper;

internal sealed class ClipPlayback : UserControl
{
    private readonly MediaElement player = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Close,
        Stretch = Stretch.Uniform,
        ScrubbingEnabled = true,
        Focusable = false,
        Volume = 0.65,
    };
    private readonly Button play = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly Button restart = new() { Text = "Restart", AutoSize = true, Enabled = false };
    private readonly TrackBar seek = new()
    {
        AccessibleName = "Preview position", Dock = DockStyle.Fill, AutoSize = false, Height = 35,
        TickStyle = TickStyle.None, Enabled = false,
    };
    private readonly Label time = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 75 };
    private TimeSpan duration;
    private bool loaded;
    private bool playing;
    private bool ended;
    private bool dragging;

    public event EventHandler? BackRequested;
    public event EventHandler? SaveRequested;

    public ClipPlayback()
    {
        Dock = DockStyle.Fill;
        var viewport = new ElementHost
        {
            AccessibleName = "Edited clip playback", Child = player, Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(17, 23, 32), TabStop = false,
        };

        var timeline = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0),
        };
        timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timeline.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timeline.Controls.Add(time, 0, 0);
        timeline.Controls.Add(status, 1, 0);
        timeline.Controls.Add(seek, 0, 1);
        timeline.SetColumnSpan(seek, 2);

        var volume = new NumericUpDown { Value = 65, Width = 55, AccessibleName = "Preview volume percentage" };
        var volumeLabel = new Label { Text = "Preview volume (%)", AutoSize = true, Margin = new Padding(12, 8, 3, 0) };
        var playback = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        playback.Controls.AddRange([play, restart, volumeLabel, volume]);

        var back = new Button { Text = "Back to edits", AutoSize = true };
        var save = new Button { Text = "Save", AutoSize = true };
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        actions.Controls.AddRange([back, save]);
        var footer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(playback, 0, 0);
        footer.Controls.Add(actions, 1, 0);

        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(viewport, 0, 0);
        layout.Controls.Add(timeline, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        play.Click += (_, _) => TogglePlayback();
        restart.Click += (_, _) => Restart();
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        volume.ValueChanged += (_, _) => player.Volume = (double)volume.Value / 100;
        seek.MouseDown += (_, _) => dragging = true;
        seek.MouseUp += (_, _) =>
        {
            dragging = false;
            Seek();
        };
        seek.Scroll += (_, _) =>
        {
            if (dragging)
                UpdateTime(SeekPosition());
            else
                Seek();
        };
        clock.Tick += (_, _) => SyncPlayhead();
        player.MediaOpened += (_, _) => PlayerOpened();
        player.MediaEnded += (_, _) =>
        {
            if (!loaded)
                return;
            ended = true;
            SetPlaying(false);
            SyncPlayhead();
        };
        player.MediaFailed += (_, args) =>
        {
            if (player.Source is not null)
                Fail(args.ErrorException?.Message ?? "Windows could not open the preview.");
        };
    }

    public void Prepare()
    {
        CloseMedia();
        status.Text = "Preparing preview…";
    }

    public void Open(string path, TimeSpan mediaDuration)
    {
        duration = mediaDuration;
        UpdateTime(TimeSpan.Zero);
        status.Text = "Opening preview…";
        player.Source = new Uri(Path.GetFullPath(path));
        player.Play();
    }

    public void Fail(string message)
    {
        CloseMedia();
        status.Text = "Preview unavailable";
        MessageBox.Show(this, message, "Could not play preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public void CloseMedia()
    {
        loaded = ended = dragging = false;
        clock.Stop();
        if (player.Source is not null)
        {
            player.Close();
            player.Source = null;
        }
        duration = TimeSpan.Zero;
        seek.Enabled = restart.Enabled = play.Enabled = false;
        seek.Value = 0;
        SetPlaying(false);
        UpdateTime(TimeSpan.Zero);
        status.Text = "";
    }

    private void PlayerOpened()
    {
        if (player.Source is null)
            return;
        if (duration <= TimeSpan.Zero || player.NaturalVideoWidth <= 0)
        {
            Fail("The preview has no playable video or duration.");
            return;
        }

        seek.Maximum = (int)Math.Clamp(Math.Ceiling(duration.TotalMilliseconds), 1, 1_000_000);
        seek.SmallChange = Math.Max(1, seek.Maximum / 300);
        seek.LargeChange = Math.Max(1, seek.Maximum / 30);
        play.Enabled = restart.Enabled = seek.Enabled = true;
        loaded = true;
        status.Text = "";
        SetPlaying(true);
        SyncPlayhead();
        clock.Start();
    }

    private void TogglePlayback()
    {
        if (!loaded)
            return;
        if (ended)
        {
            Restart();
            return;
        }
        if (playing)
            player.Pause();
        else
            player.Play();
        SetPlaying(!playing);
    }

    private void Restart()
    {
        if (!loaded)
            return;
        ended = false;
        player.Position = TimeSpan.Zero;
        player.Play();
        SetPlaying(true);
        SyncPlayhead();
    }

    private void SetPlaying(bool value)
    {
        playing = value;
        play.Text = value ? "Pause" : ended ? "Replay" : "Play";
    }

    private void Seek()
    {
        if (!loaded)
            return;
        var target = SeekPosition();
        ended = false;
        SetPlaying(playing);
        player.Position = target == duration && duration.TotalMilliseconds >= 1
            ? target - TimeSpan.FromMilliseconds(1) : target;
        UpdateTime(target);
    }

    private TimeSpan SeekPosition() => TimeSpan.FromTicks((long)(duration.Ticks * (seek.Value / (double)seek.Maximum)));

    private void SyncPlayhead()
    {
        if (!loaded || dragging)
            return;
        var position = ended || player.Position > duration ? duration : player.Position;
        seek.Value = (int)Math.Clamp(Math.Round(position.Ticks / (double)duration.Ticks * seek.Maximum), 0, seek.Maximum);
        UpdateTime(position);
    }

    private void UpdateTime(TimeSpan position) => time.Text = $"{ClipEditor.Clock(position)} / {ClipEditor.Clock(duration)}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseMedia();
            clock.Dispose();
        }
        base.Dispose(disposing);
    }
}
