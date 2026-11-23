using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Muro.App;

/// The picture for an interactive wallpaper you imported, which has none of its own: a dark gradient with its name. It
/// is the card's thumbnail, and what plays wherever the page cannot (lock screen, screen saver, a frozen screen).
internal static class PlaceholderStill
{
    public static (byte[] Still, byte[] Thumb) Make(string title) => (Jpeg(1920, 1080, title), Jpeg(480, 270, title));

    private static byte[] Jpeg(int width, int height, string title)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(24, 18, 48), Color.FromArgb(8, 60, 66), 35f))
                g.FillRectangle(brush, 0, 0, width, height);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", height / 14f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            var box = new RectangleF(width * 0.08f, 0, width * 0.84f, height);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            g.DrawString(title, font, white, box, format);
        }
        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 88L) } };
        bitmap.Save(stream, encoder, parameters);
        return stream.ToArray();
    }
}
