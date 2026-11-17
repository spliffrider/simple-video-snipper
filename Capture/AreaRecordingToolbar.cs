using System.Diagnostics;

namespace SimpleVideoSnipper;

internal sealed class AreaRecordingToolbar : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };

    public AreaRecordingToolbar(SelectedArea area, RecordingHost.RecordingSession session)
    {
        Text = "Area recording";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Location = area.Display.Location;

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
        controls.Controls.AddRange([elapsed, button]);
        Controls.Add(controls);

        Shown += (_, _) => PlaceOutsideSelection(area);
        timer.Tick += (_, _) =>
        {
            var finishing = session.StopRequested.IsCompleted;
            button.Enabled = !finishing;
            elapsed.Text = finishing ? "Finishing…" : $"Recording  {clock.Elapsed:hh\\:mm\\:ss}";
        };
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    private void PlaceOutsideSelection(SelectedArea area)
    {
        var work = Screen.FromRectangle(area.Display).WorkingArea;
        var region = area.DesktopRegion;
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
