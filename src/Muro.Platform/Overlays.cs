using System.Drawing;
using Muro.Core;

namespace Muro.Platform;

/// A click-through sheet of one colour laid over a screen's wallpaper, faded with a constant alpha. The desktop
/// compositor blends it, so it costs the app no GPU work and no per-frame CPU: used for the night tint and the
/// audio flash, since mpv's own brightness control does nothing in the low-cost playback mode.
public sealed class TintLayer : IDisposable
{
    private readonly FadeOverlay _overlay;

    public TintLayer(DesktopHost.Target target, Rect screen, Color colour)
    {
        var swatch = new Bitmap(16, 16);   // solid, so stretching it over the screen blends nothing at the edges
        using (var g = Graphics.FromImage(swatch)) g.Clear(colour);
        _overlay = new FadeOverlay(target, (int)screen.X, (int)screen.Y, (int)screen.Width, (int)screen.Height, swatch);
        _overlay.SetAlpha(0);
    }

    /// 0 invisible, 255 solid.
    public void SetAlpha(byte alpha) => _overlay.SetAlpha(alpha);

    public void Dispose() => _overlay.Dispose();
}

