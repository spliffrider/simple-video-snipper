using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;

namespace SimpleVideoSnipper;

internal enum GifCaptionPosition
{
    Top,
    Center,
    Bottom,
    Custom,
}

internal sealed record GifCaption(string Text)
{
    public const string DefaultFont = "Segoe UI";

    public GifCaptionPosition Position { get; init; } = GifCaptionPosition.Bottom;
    public PointF CustomPosition { get; init; } = new(0.5f, 1);
    public string FontName { get; init; } = DefaultFont;
    public int? FontSize { get; init; }
    public bool Bold { get; init; } = true;
    public Color TextColor { get; init; } = Color.White;
    public bool Outline { get; init; }
    public Color OutlineColor { get; init; } = Color.Black;
    public int OutlineWidth { get; init; } = 2;
    public bool Background { get; init; } = true;

    public static Size ScaleFrame(Size source, int longestSide)
    {
        var scale = Math.Min(1, longestSide / (double)Math.Max(source.Width, source.Height));
        return new Size(
            Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero)));
    }

    public Rectangle WriteOverlay(string path, Size frame)
    {
        var shortSide = Math.Min(frame.Width, frame.Height);
        var outline = Outline ? OutlineWidth : 0;
        var padding = (Background ? Math.Clamp(shortSide / 35, 2, 10) : 0) + outline;
        var maxWidth = frame.Width - 2 * padding;
        var maxHeight = frame.Height - 2 * padding;
        if (maxWidth <= 0 || maxHeight <= 0)
            throw new ArgumentException("Choose a larger crop or a thinner outline to fit the GIF caption.");

        var fill = Brush(TextColor);
        var typeface = new Typeface(new System.Windows.Media.FontFamily(FontName), FontStyles.Normal,
            Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var preferredSize = FontSize ?? Math.Clamp(shortSide / 10, 8, 46);
        for (var size = preferredSize; size >= (FontSize ?? 8); size--)
        {
            var layout = new FormattedText(Text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                typeface, size, fill, pixelsPerDip: 1)
            {
                TextAlignment = TextAlignment.Center,
                Trimming = TextTrimming.None,
                MaxTextWidth = maxWidth,
            };
            if (layout.Height > maxHeight + size)
                continue;
            var glyphs = layout.BuildGeometry(new System.Windows.Point());
            var ink = glyphs.Bounds;
            if (ink.IsEmpty || ink.Width > maxWidth || ink.Height > maxHeight)
                continue;

            var bounds = Place(frame, new Size((int)Math.Ceiling(ink.Width) + 2 * padding,
                (int)Math.Ceiling(ink.Height) + 2 * padding));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                if (Background)
                    drawing.DrawRectangle(Brush(Color.FromArgb(196, Color.Black)), null,
                        new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                drawing.PushTransform(new TranslateTransform(bounds.Left + padding - ink.X, bounds.Top + padding - ink.Y));
                if (outline > 0)
                    drawing.DrawGeometry(null, new System.Windows.Media.Pen(Brush(OutlineColor), outline * 2)
                    {
                        LineJoin = PenLineJoin.Round,
                    }, glyphs);
                drawing.DrawGeometry(fill, null, glyphs);
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new FileStream(path, FileMode.CreateNew);
            encoder.Save(output);
            return bounds;
        }

        throw new ArgumentException("The caption does not fit this GIF. Use a smaller font, shorter text, or larger crop.");
    }

    private Rectangle Place(Size frame, Size box)
    {
        var freeWidth = frame.Width - box.Width;
        var freeHeight = frame.Height - box.Height;
        var margin = Math.Max(1, Math.Min(frame.Width, frame.Height) / 24);
        var x = Position == GifCaptionPosition.Custom
            ? (int)Math.Round(Math.Clamp(CustomPosition.X, 0, 1) * freeWidth)
            : freeWidth / 2;
        var y = Position switch
        {
            GifCaptionPosition.Top => Math.Min(margin, freeHeight),
            GifCaptionPosition.Center => freeHeight / 2,
            GifCaptionPosition.Custom => (int)Math.Round(Math.Clamp(CustomPosition.Y, 0, 1) * freeHeight),
            _ => Math.Max(0, freeHeight - margin),
        };
        return new Rectangle(x, y, box.Width, box.Height);
    }

    private static SolidColorBrush Brush(Color color) =>
        new(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
}
