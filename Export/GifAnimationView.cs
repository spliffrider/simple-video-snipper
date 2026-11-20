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

    public GifAnimationView()
    {
        BackColor = Color.FromArgb(18, 23, 32);
        DoubleBuffered = true;
        ResizeRedraw = true;
        clock.Tick += (_, _) => NextFrame();
    }

    public Size ImageSize => image?.Size ?? Size.Empty;
    public bool IsPlaying => clock.Enabled;

    public void Open(string path)
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
        actualSize = value;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        AutoScrollPosition = Point.Empty;
        AutoScrollMinSize = actualSize ? ImageSize : Size.Empty;
        AutoScroll = actualSize;
        Invalidate();
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
        if (image is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        var size = image.Size;
        if (!actualSize)
        {
            var scale = Math.Min(ClientSize.Width / (double)size.Width, ClientSize.Height / (double)size.Height);
            size = new Size(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        }
        var scroll = AutoScrollPosition;
        var x = Math.Max(0, (ClientSize.Width - size.Width) / 2) + scroll.X;
        var y = Math.Max(0, (ClientSize.Height - size.Height) / 2) + scroll.Y;
        e.Graphics.InterpolationMode = actualSize ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(image, new Rectangle(x, y, size.Width, size.Height));
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
