using System.Diagnostics;

namespace SimpleVideoSnipper;

internal sealed class RecordingToolbar : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Label warning = new() { AutoSize = true };

    public RecordingToolbar(CaptureTarget target, RecordingHost.RecordingSession session)
    {
        Text = target.IsWindow ? "Window recording" : "Area recording";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Location = Screen.FromRectangle(target.Bounds).Bounds.Location;

        var clock = Stopwatch.StartNew();
        var elapsed = new Label { Text = "Recording  00:00", AutoSize = true, Anchor = AnchorStyles.Left };
        var button = new Button { Text = "Stop recording", AutoSize = true };
        button.Click += (_, _) => session.RequestStop();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                session.RequestStop();
            }
        };
        var controls = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(8),
        };
        controls.Controls.AddRange([elapsed, button, warning]);
        Controls.Add(controls);

        Shown += (_, _) => PlaceOutsideSelection(target.Bounds);
        timer.Tick += (_, _) =>
        {
            var finishing = session.StopRequested.IsCompleted;
            button.Enabled = !finishing;
            elapsed.Text = finishing ? "Finishing…" : $"Recording  {clock.Elapsed:hh\\:mm\\:ss}";
        };
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowAudioWarning(string message) => warning.Text = message;

    private void PlaceOutsideSelection(Rectangle region)
    {
        var work = Screen.FromRectangle(region).WorkingArea;
        var x = Math.Clamp(region.Left, work.Left, Math.Max(work.Left, work.Right - Width));
        var y = Math.Clamp(region.Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        Point[] positions =
        [
            new(x, region.Top - Height - 8),
            new(x, region.Bottom + 8),
            new(region.Right + 8, y),
            new(region.Left - Width - 8, y),
        ];
        foreach (var candidate in positions)
        {
            if (work.Contains(new Rectangle(candidate, Size)))
            {
                Location = candidate;
                return;
            }
        }

        Location = new Point(work.Right - Width, work.Top);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            timer.Dispose();
        base.Dispose(disposing);
    }
}
