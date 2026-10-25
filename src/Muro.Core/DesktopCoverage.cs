namespace Muro.Core;

/// Which monitors have an app window open on them. Port of the macOS rule: only position and size
/// are read, never what is in a window. The platform layer has already dropped non-app windows
/// (cloaked, minimised, tool windows, shell chrome).
///
/// shortcut: thresholds are physical pixels, not DIPs, so at 150% scaling they are 1.5x looser
/// than on macOS. Scale them per monitor if that ever matters.
public static class DesktopCoverage
{
    /// A window smaller than this on either side is not read as covering a desktop.
    public const double MinimumSide = 40;
    /// A window covers a monitor when a quarter of it is there, or a piece at least
    /// <see cref="MinimumPiece"/> on both sides (keeps a huge window counting on a monitor that
    /// shows only a fraction of it).
    public const double MinimumShare = 0.25;
    public const double MinimumPiece = 200;

    public static bool Counts(Rect window) => window.Width >= MinimumSide && window.Height >= MinimumSide;

    public static bool Covers(Rect window, Rect screen)
    {
        if (window.Width <= 0 || window.Height <= 0) return false;
        if (screen.Intersect(window) is not { } overlap) return false;
        var share = overlap.Width * overlap.Height / (window.Width * window.Height);
        return share >= MinimumShare || (overlap.Width >= MinimumPiece && overlap.Height >= MinimumPiece);
    }

    /// A window across two monitors covers both.
    public static HashSet<TKey> CoveredScreens<TKey>(IEnumerable<Rect> windows, IReadOnlyDictionary<TKey, Rect> screens)
        where TKey : notnull
    {
        var covered = new HashSet<TKey>();
        foreach (var window in windows.Where(Counts))
            foreach (var (key, frame) in screens)
                if (Covers(window, frame)) covered.Add(key);
        return covered;
    }

    /// A monitor that has just gone clear stays reported covered until a second look agrees, so a
    /// window caught mid-animation cannot start a wallpaper for a moment. A monitor that has just
    /// been covered is reported at once: freezing late costs nothing, playing by mistake does.
    public static (HashSet<TKey> Report, bool NeedsConfirmation) Settle<TKey>(
        HashSet<TKey>? previous, HashSet<TKey> current, bool confirming) where TKey : notnull
    {
        if (previous is null || confirming) return (current, false);
        var cleared = previous.Except(current).ToList();
        if (cleared.Count == 0) return (current, false);
        var report = new HashSet<TKey>(current);
        report.UnionWith(cleared);
        return (report, true);
    }

    /// "Play only on desktop": hold a monitor's wallpaper while a window is open on it. A monitor
    /// nobody has looked at yet is never held.
    public static bool Holds(bool? isCovered, bool playOnlyOnDesktop) => playOnlyOnDesktop && isCovered == true;
}
