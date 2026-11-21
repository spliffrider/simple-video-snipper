using System.Drawing.Imaging;
using System.Drawing.Text;

namespace SimpleVideoSnipper;

internal enum GifCaptionPosition
{
    Top,
    Center,
    Bottom,
}

internal sealed record GifCaption(string Text, GifCaptionPosition Position)
{
    public void WriteOverlay(string path, Size source, int longestSide)
    {
        var scale = Math.Min(1, longestSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));
        var shortSide = Math.Min(width, height);
        var margin = Math.Max(1, shortSide / 24);
        var padding = Math.Clamp(shortSide / 35, 2, 10);
        var textArea = new SizeF(width - 2 * (margin + padding), height - 2 * (margin + padding));
        if (textArea.Width <= 0 || textArea.Height <= 0)
            throw new ArgumentException("Choose a larger crop to fit the GIF caption.");

        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.LineLimit,
        };
        var maxFontSize = Math.Clamp(shortSide / 10, 8, 46);
        for (var fontSize = maxFontSize; fontSize >= 8; fontSize--)
        {
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(Text, font, textArea, format, out var fitted, out _);
            if (fitted < Text.Length)
                continue;

            var boxWidth = Math.Min(width - 2 * margin, (int)Math.Ceiling(measured.Width) + 2 * padding);
            var boxHeight = Math.Min(height - 2 * margin, (int)Math.Ceiling(measured.Height) + 2 * padding);
            var top = Position switch
            {
                GifCaptionPosition.Top => margin,
                GifCaptionPosition.Center => (height - boxHeight) / 2,
                _ => height - boxHeight - margin,
            };
            var box = new Rectangle((width - boxWidth) / 2, top, boxWidth, boxHeight);
            using var background = new SolidBrush(Color.FromArgb(196, Color.Black));
            graphics.FillRectangle(background, box);
            box.Inflate(-padding, -padding);
            graphics.DrawString(Text, font, Brushes.White, box, format);
            image.Save(path, ImageFormat.Png);
            return;
        }

        throw new ArgumentException("The caption is too long for this GIF. Shorten the text or choose a larger crop.");
    }
}
