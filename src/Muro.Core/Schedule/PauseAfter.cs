namespace Muro.Core.Schedule;

/// How long a wallpaper keeps moving before it freezes on a frame.
///
/// The rule has one trap, so it lives here written down and tested:
/// - a wallpaper with no value of its own follows the global setting;
/// - a wallpaper set to zero never freezes, and must NOT fall back to the global setting: "never
///   pause" is a decision, not an absence;
/// - a global setting of zero or nothing means nothing freezes on its own.
public static class PauseAfter
{
    /// Seconds to play before freezing, or null for "keep playing".
    public static int? Resolve(int? wallpaper, int? global) => Normalise(wallpaper ?? global);

    /// Anything at or below zero means "never freeze".
    public static int? Normalise(int? seconds) => seconds is > 0 ? seconds : null;
}

public static class FrameRate
{
    private static readonly int[] Rates = [24, 25, 30, 50, 60, 72, 90, 120, 144, 240];

    /// The frame rate to show and filter by: the nearest standard rate. Wallpapers report exact rates,
    /// and many sit a little off a round number (59.94, 29.97), so the label and filter use this.
    public static int Standard(double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0) return 0;
        return Rates.MinBy(s => Math.Abs(s - fps));
    }
}
