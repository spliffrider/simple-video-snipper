using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SimpleVideoSnipper;

internal sealed class GifAnimationView : ScrollableControl
{
    private const int FrameDelayProperty = 0x5100;
    private readonly System.Windows.Forms.Timer clock = new();
    private FileStream? stream;
    private Image? image;
    private byte[]? delays;
    private int frame;
    private int frameCount;
    private bool actualSize;
    private Point scrollOffset;
    private Rectangle captionBounds;
    private Rectangle? draggedCaption;
    private Point dragOffset;
    private bool captionHovered;

    public GifAnimationView()
    {
        BackColor = Color.FromArgb(18, 23, 32);
        DoubleBuffered = true;
        ResizeRedraw = true;
        clock.Tick += (_, _) => NextFrame();
    }

    public Size ImageSize => image?.Size ?? Size.Empty;
    public bool IsPlaying => clock.Enabled;
    public bool IsDraggingCaption => draggedCaption is not null;
    public event Action<PointF>? CaptionMoved;

    public void Open(string path, Rectangle caption)
    {
        Clear();
        stream = File.OpenRead(path);
        try
        {
            image = Image.FromStream(stream);
            frame = 0;
            frameCount = image.FrameDimensionsList.Contains(FrameDimension.Time.Guid)
                ? image.GetFrameCount(FrameDimension.Time) : 1;
            delays = image.PropertyIdList.Contains(FrameDelayProperty)
                ? image.GetPropertyItem(FrameDelayProperty)?.Value : null;
            captionBounds = caption;
            UpdateViewport();
            SetPlaying(true);
        }
        catch
        {
            Clear();
            throw;
        }
    }

    public void Clear()
    {
        RememberScrollPosition();
        EndCaptionDrag();
        captionBounds = Rectangle.Empty;
        SetCaptionHovered(false);
        clock.Stop();
        image?.Dispose();
        image = null;
        stream?.Dispose();
        stream = null;
        delays = null;
        frameCount = 0;
        UpdateViewport();
    }

    public void SetActualSize(bool value)
    {
        RememberScrollPosition();
        actualSize = value;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        AutoScrollPosition = Point.Empty;
        AutoScrollMinSize = actualSize ? ImageSize : Size.Empty;
        AutoScroll = actualSize;
        if (actualSize && image is not null)
            AutoScrollPosition = scrollOffset;
        Invalidate();
    }

    private void RememberScrollPosition()
    {
        if (actualSize && image is not null)
            scrollOffset = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
    }

    public void SetPlaying(bool playing)
    {
        if (playing && frameCount > 1)
        {
            clock.Interval = FrameDelay();
            clock.Start();
        }
        else
            clock.Stop();
    }

    private void NextFrame()
    {
        if (image is null || frameCount < 2)
            return;
        frame = (frame + 1) % frameCount;
        image.SelectActiveFrame(FrameDimension.Time, frame);
        clock.Interval = FrameDelay();
        Invalidate();
    }

    private int FrameDelay()
    {
        var offset = frame * sizeof(uint);
        if (delays is null || offset > delays.Length - sizeof(uint))
            return 100;
        var centiseconds = BinaryPrimitives.ReadUInt32LittleEndian(delays.AsSpan(offset, sizeof(uint)));
        return (int)Math.Clamp(centiseconds, 1u, 65535u) * 10;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = DisplayBounds();
        if (image is null || bounds.IsEmpty)
            return;

        e.Graphics.InterpolationMode = actualSize ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(image, bounds);
        if (captionHovered || draggedCaption is not null)
        {
            var guide = CaptionOnScreen(draggedCaption ?? captionBounds, bounds);
            using var shadow = new Pen(Color.Black, 3);
            using var outline = new Pen(Color.White) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(shadow, guide.X, guide.Y, guide.Width, guide.Height);
            e.Graphics.DrawRectangle(outline, guide.X, guide.Y, guide.Width, guide.Height);
        }
    }

    private Rectangle DisplayBounds()
    {
        if (image is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return Rectangle.Empty;
        var size = image.Size;
        if (!actualSize)
        {
            var scale = Math.Min(ClientSize.Width / (double)size.Width, ClientSize.Height / (double)size.Height);
            size = new Size(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        }
        var scroll = AutoScrollPosition;
        var x = Math.Max(0, (ClientSize.Width - size.Width) / 2) + scroll.X;
        var y = Math.Max(0, (ClientSize.Height - size.Height) / 2) + scroll.Y;
        return new Rectangle(x, y, size.Width, size.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var bounds = DisplayBounds();
        if (e.Button != MouseButtons.Left || !HitCaption(e.Location, bounds))
            return;

        var point = ImagePoint(e.Location, bounds);
        dragOffset = new Point(point.X - captionBounds.X, point.Y - captionBounds.Y);
        draggedCaption = captionBounds;
        Capture = true;
        SetCaptionHovered(true);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var bounds = DisplayBounds();
        if (draggedCaption is not null && !bounds.IsEmpty)
        {
            draggedCaption = MoveCaption(e.Location, bounds);
            Invalidate();
        }
        else
            SetCaptionHovered(HitCaption(e.Location, bounds));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var bounds = DisplayBounds();
        if (e.Button != MouseButtons.Left || draggedCaption is null || bounds.IsEmpty)
            return;

        var next = MoveCaption(e.Location, bounds);
        EndCaptionDrag();
        SetCaptionHovered(HitCaption(e.Location, bounds));
        Invalidate();
        if (next.Location != captionBounds.Location)
        {
            var width = ImageSize.Width - next.Width;
            var height = ImageSize.Height - next.Height;
            CaptionMoved?.Invoke(new PointF(width == 0 ? 0.5f : next.X / (float)width,
                height == 0 ? 0.5f : next.Y / (float)height));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (draggedCaption is null)
            SetCaptionHovered(false);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && draggedCaption is not null)
        {
            EndCaptionDrag();
            SetCaptionHovered(false);
            Invalidate();
        }
    }

    private void EndCaptionDrag()
    {
        if (draggedCaption is null)
            return;
        draggedCaption = null;
        Capture = false;
    }

    private void SetCaptionHovered(bool hovered)
    {
        if (captionHovered == hovered)
            return;
        captionHovered = hovered;
        Cursor = hovered ? Cursors.SizeAll : Cursors.Default;
        Invalidate();
    }

    private bool HitCaption(Point point, Rectangle bounds) =>
        !bounds.IsEmpty && !captionBounds.IsEmpty && CaptionOnScreen(captionBounds, bounds).Contains(point);

    private RectangleF CaptionOnScreen(Rectangle caption, Rectangle bounds)
    {
        var sx = bounds.Width / (float)ImageSize.Width;
        var sy = bounds.Height / (float)ImageSize.Height;
        return new RectangleF(bounds.X + caption.X * sx, bounds.Y + caption.Y * sy, caption.Width * sx, caption.Height * sy);
    }

    private Point ImagePoint(Point point, Rectangle bounds) => new(
        (int)Math.Round((point.X - bounds.X) * (double)ImageSize.Width / bounds.Width),
        (int)Math.Round((point.Y - bounds.Y) * (double)ImageSize.Height / bounds.Height));

    private Rectangle MoveCaption(Point location, Rectangle bounds)
    {
        var point = ImagePoint(location, bounds);
        return new Rectangle(Math.Clamp(point.X - dragOffset.X, 0, ImageSize.Width - captionBounds.Width),
            Math.Clamp(point.Y - dragOffset.Y, 0, ImageSize.Height - captionBounds.Height), captionBounds.Width, captionBounds.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            clock.Dispose();
            image?.Dispose();
            stream?.Dispose();
        }
        base.Dispose(disposing);
    }
}
