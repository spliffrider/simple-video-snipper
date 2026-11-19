namespace SimpleVideoSnipper;

internal sealed class TrimTimeline : Control
{
    private enum DragTarget { None, Start, End, Playhead }

    private readonly int minimumSpan;
    private DragTarget dragging;
    private int dragOffset;

    public TrimTimeline(TimeSpan duration)
    {
        var milliseconds = duration.TotalMilliseconds;
        Maximum = (int)Math.Clamp(Math.Ceiling(milliseconds), 1, 1_000_000);
        minimumSpan = Math.Clamp((int)Math.Ceiling(Maximum * Math.Min(1, 100 / milliseconds)), 1, Maximum);
        End = Maximum;
        AccessibleName = "Recording timeline";
        AccessibleDescription = "Drag the left or right handle to trim. Arrow keys scrub. Set start and Set end use the current position.";
        AccessibleRole = AccessibleRole.Slider;
        Height = 38;
        Dock = DockStyle.Fill;
        TabStop = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public int Maximum { get; }
    public int Start { get; private set; }
    public int End { get; private set; }
    public int Value { get; private set; }

    public bool CanSetStart => Value <= End - minimumSpan;
    public bool CanSetEnd => Value >= Start + minimumSpan;
    public bool IsTrimmed => Start != 0 || End != Maximum;
    public event EventHandler? Changed;

    public void SetStartAtPlayhead()
    {
        if (CanSetStart)
            UpdateRange(Value, End, Value);
    }

    public void SetEndAtPlayhead()
    {
        if (CanSetEnd)
            UpdateRange(Start, Value, Value);
    }

    public void ResetTrim() => UpdateRange(0, Maximum, Value);

    public void ShowPlaybackPosition(int position)
    {
        var next = Math.Clamp(position, 0, Maximum);
        if (next == Value)
            return;
        Value = next;
        Invalidate();
    }

    public void Scrub(int position) => UpdateRange(Start, End, Math.Clamp(position, 0, Maximum));

    private int Inset => LogicalToDeviceUnits(16);
    private int TrackWidth => Math.Max(1, ClientSize.Width - Inset * 2);
    private int X(int time) => Inset + (int)Math.Round(TrackWidth * (time / (double)Maximum));
    private int TimeAt(int x) => (int)Math.Round(Maximum * Math.Clamp((x - Inset) / (double)TrackWidth, 0, 1));

    private Rectangle HandleBounds(int position, bool start)
    {
        var width = LogicalToDeviceUnits(12);
        var height = LogicalToDeviceUnits(26);
        return new Rectangle(X(position) - (start ? width : 0), (ClientSize.Height - height) / 2, width, height);
    }

    private void UpdateRange(int start, int end, int position)
    {
        if (Start == start && End == end && Value == position)
            return;
        Start = start;
        End = end;
        Value = position;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void DragTo(int x)
    {
        var time = TimeAt(x - dragOffset);
        switch (dragging)
        {
            case DragTarget.Start:
                var start = Math.Min(time, End - minimumSpan);
                UpdateRange(start, End, start);
                break;
            case DragTarget.End:
                var end = Math.Max(time, Start + minimumSpan);
                UpdateRange(Start, end, end);
                break;
            case DragTarget.Playhead:
                Scrub(time);
                break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        Focus();
        dragging = HandleBounds(Start, true).Contains(e.Location) ? DragTarget.Start
            : HandleBounds(End, false).Contains(e.Location) ? DragTarget.End : DragTarget.Playhead;
        dragOffset = dragging switch
        {
            DragTarget.Start => e.X - X(Start),
            DragTarget.End => e.X - X(End),
            _ => 0,
        };
        Capture = true;
        DragTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging != DragTarget.None)
            DragTo(e.X);
        Cursor = dragging is DragTarget.Start or DragTarget.End || HandleBounds(Start, true).Contains(e.Location) ||
            HandleBounds(End, false).Contains(e.Location) ? Cursors.SizeWE : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || dragging == DragTarget.None)
            return;
        DragTo(e.X);
        dragging = DragTarget.None;
        Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
            dragging = DragTarget.None;
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Math.Max(1, Maximum / 300);
        var position = e.KeyCode switch
        {
            Keys.Left => Value - step,
            Keys.Right => Value + step,
            Keys.Home => 0,
            Keys.End => Maximum,
            _ => (int?)null,
        };
        if (position is { } next)
        {
            Scrub(next);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var laneHeight = LogicalToDeviceUnits(6);
        var center = ClientSize.Height / 2;
        var laneY = center - laneHeight / 2;
        var selected = X(Start);
        using var rangeBrush = new SolidBrush(Color.FromArgb(110, SystemColors.Highlight));
        using var playhead = new Pen(SystemColors.WindowText, LogicalToDeviceUnits(2));
        e.Graphics.FillRectangle(SystemBrushes.ControlDark, Inset, laneY, TrackWidth, laneHeight);
        e.Graphics.FillRectangle(rangeBrush, selected, laneY, Math.Max(1, X(End) - selected), laneHeight);
        e.Graphics.DrawLine(playhead, X(Value), center - LogicalToDeviceUnits(13), X(Value), center + LogicalToDeviceUnits(13));
        DrawHandle(HandleBounds(Start, true));
        DrawHandle(HandleBounds(End, false));
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);

        void DrawHandle(Rectangle bounds)
        {
            e.Graphics.FillRectangle(SystemBrushes.Highlight, bounds);
            var grip = bounds.Left + bounds.Width / 2;
            var gripHeight = LogicalToDeviceUnits(5);
            e.Graphics.DrawLine(SystemPens.HighlightText, grip, center - gripHeight, grip, center + gripHeight);
        }
    }
}
