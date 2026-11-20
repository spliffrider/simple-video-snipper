namespace SimpleVideoSnipper;

internal readonly record struct SelectedArea(nint Monitor, Rectangle Display, Rectangle Region)
{
    public Rectangle DesktopRegion =>
        new(Display.Left + Region.Left, Display.Top + Region.Top, Region.Width, Region.Height);

    public static Rectangle RegionFromDrag(Size display, Point start, Point end)
    {
        end.X = Math.Clamp(end.X, 0, display.Width);
        end.Y = Math.Clamp(end.Y, 0, display.Height);
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        // H.264 4:2:0 requires even frame dimensions. The outline uses those same edges.
        var width = Math.Abs(end.X - start.X) & ~1;
        var height = Math.Abs(end.Y - start.Y) & ~1;
        return new Rectangle(left, top, width, height);
    }
}

internal sealed class AreaSelectionOverlay : Form
{
    private const int MinimumSide = 32;
    private readonly Rectangle display;
    private readonly nint monitor;
    private readonly Bitmap snapshot;
    private readonly FlowLayoutPanel actions;
    private readonly bool startOnRelease;
    private Rectangle area;
    private Point? anchor;

    public AreaSelectionOverlay(Screen screen, bool startOnRelease)
    {
        this.startOnRelease = startOnRelease;
        display = screen.Bounds;
        monitor = NativeCaptureMethods.MonitorFromPoint(
            new Point(display.Left + display.Width / 2, display.Top + display.Height / 2), 0);
        snapshot = new Bitmap(display.Width, display.Height);
        try
        {
            using var graphics = Graphics.FromImage(snapshot);
            graphics.CopyFromScreen(display.Location, Point.Empty, display.Size);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }

        Text = "Select recording area";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = display;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;

        var record = new Button { Text = "Continue", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(4),
            BackColor = SystemColors.Window,
            Cursor = Cursors.Default,
            Visible = false,
        };
        actions.Controls.AddRange([record, cancel]);
        Controls.Add(actions);
        AcceptButton = record;
        CancelButton = cancel;
    }

    public SelectedArea Selection => new(monitor, display, area);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = display;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(snapshot, 0, 0);
        using var shade = new SolidBrush(Color.FromArgb(158, 0, 0, 0));
        e.Graphics.FillRectangle(shade, ClientRectangle);
        if (area.Width > 0 && area.Height > 0)
        {
            e.Graphics.DrawImage(snapshot, area, area, GraphicsUnit.Pixel);
            using var outline = new Pen(Color.DeepSkyBlue, 2);
            e.Graphics.DrawRectangle(outline, area.X, area.Y, area.Width - 1, area.Height - 1);
            if (anchor is null && actions.Visible)
            {
                using var handle = new SolidBrush(Color.DeepSkyBlue);
                e.Graphics.FillRectangle(handle, ResizeHandle);
            }
        }

        var text = anchor is not null || actions.Visible
            ? $"{area.Width} × {area.Height} px"
            : startOnRelease ? "Drag to start recording. Press Esc to cancel."
                : "Drag to select an area. Press Esc to cancel.";
        TextRenderer.DrawText(e.Graphics, text, Font, new Point(12, 12), Color.White, Color.Black);
    }

    private Rectangle ResizeHandle => new(area.Right - 7, area.Bottom - 7, 14, 14);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        anchor = actions.Visible && ResizeHandle.Contains(e.Location) ? area.Location : e.Location;
        actions.Hide();
        Capture = true;
        UpdateArea(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (anchor is not null)
            UpdateArea(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || anchor is null)
            return;

        UpdateArea(e.Location);
        FinishDrag(released: true);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (anchor is not null && !Capture)
            FinishDrag(released: false);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    private void FinishDrag(bool released)
    {
        anchor = null;
        Capture = false;
        if (area.Width >= MinimumSide && area.Height >= MinimumSide && (!startOnRelease || released))
        {
            if (startOnRelease)
            {
                DialogResult = DialogResult.OK;
                return;
            }
            var toolbar = actions.GetPreferredSize(Size.Empty);
            var x = Math.Clamp(area.Left, 0, Math.Max(0, ClientSize.Width - toolbar.Width));
            var y = area.Bottom + 10;
            if (y + toolbar.Height > ClientSize.Height)
                y = Math.Max(0, area.Top - toolbar.Height - 10);
            actions.Location = new Point(x, y);
            actions.Show();
        }
        else
        {
            area = Rectangle.Empty;
        }

        Invalidate();
    }

    private void UpdateArea(Point end)
    {
        area = SelectedArea.RegionFromDrag(display.Size, anchor.GetValueOrDefault(), end);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            snapshot.Dispose();
        base.Dispose(disposing);
    }
}
