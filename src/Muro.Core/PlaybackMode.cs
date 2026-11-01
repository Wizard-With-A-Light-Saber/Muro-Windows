using Muro.Core.Library;

namespace Muro.Core;

/// Which playback mode a wallpaper gets. "auto" (the default) decides per wallpaper: Efficient, a lighter copy
/// (30 fps, and shrunk to the screen when it is much bigger), for the heaviest ones, Smooth for the rest. "smooth" and "efficient" are the user's own explicit choice and win.
public static class PlaybackMode
{
    public const string Auto = "auto", Smooth = "smooth", Efficient = "efficient";

    /// 4K at 50 fps or more (about 8 megapixels at 60 fps): on a laptop it costs 3 to 4% of the whole CPU, against
    /// about 1.5% as a 30 fps copy. 1440p at 60 fps (3.7 megapixels) and 4K at 30 fps stay Smooth.
    public static bool IsHeavy(int width, int height, double fps) => fps >= 50 && (long)width * height >= 6_000_000;

    /// <paramref name="choice"/> is the setting: auto, smooth or efficient (anything else counts as auto).
    public static string Resolve(string? choice, WallpaperEntry? entry) => choice switch
    {
        Efficient => Efficient,
        Smooth => Smooth,
        _ => entry is not null && (IsHeavy(entry.Width, entry.Height, entry.Fps) || Media.EfficientVariant.TargetSize(entry.Width, entry.Height) is not null) ? Efficient : Smooth,
    };
}
