using Muro.Core;

namespace Muro.Core.Tests;

public class DesktopCoverageTests
{
    private static readonly Rect Left = new(0, 0, 1920, 1080);
    private static readonly Rect Right = new(1920, 0, 1920, 1080);
    private static readonly Dictionary<string, Rect> Screens = new() { ["L"] = Left, ["R"] = Right };

    [Fact] public void TinyHelperWindow_DoesNotCount() => Assert.False(DesktopCoverage.Counts(new Rect(0, 0, 1, 1)));

    [Fact] public void ThinStrip_DoesNotCover() =>
        Assert.False(DesktopCoverage.Covers(new Rect(-1900, 100, 1920, 800), Left));   // 20 px sliver of a big window

    [Fact] public void WindowOnOneScreen_CoversOnlyThatScreen()
    {
        var covered = DesktopCoverage.CoveredScreens([new Rect(100, 100, 800, 600)], Screens);
        Assert.Equal(["L"], covered);
    }

    [Fact] public void WindowAcrossTwoScreens_CoversBoth()
    {
        var covered = DesktopCoverage.CoveredScreens([new Rect(1500, 100, 1000, 800)], Screens);
        Assert.Equal(2, covered.Count);
    }

    [Fact] public void HugeWindowWithBigPiece_StillCovers() =>
        Assert.True(DesktopCoverage.Covers(new Rect(-3000, 0, 6000, 4000), Left));   // share < 25% but piece >= 200x200

    [Fact] public void Settle_JustClearedIsHeldUntilConfirmed()
    {
        var (report, confirm) = DesktopCoverage.Settle(new HashSet<string> { "L" }, new HashSet<string>(), confirming: false);
        Assert.Equal(["L"], report);
        Assert.True(confirm);
        var (final, again) = DesktopCoverage.Settle(new HashSet<string> { "L" }, new HashSet<string>(), confirming: true);
        Assert.Empty(final);
        Assert.False(again);
    }

    [Fact] public void Settle_JustCoveredReportsAtOnce()
    {
        var (report, confirm) = DesktopCoverage.Settle(new HashSet<string>(), new HashSet<string> { "L" }, false);
        Assert.Equal(["L"], report);
        Assert.False(confirm);
    }

    [Fact] public void Holds_RequiresSwitchAndKnownCovered()
    {
        Assert.False(DesktopCoverage.Holds(true, false));
        Assert.False(DesktopCoverage.Holds(null, true));
        Assert.True(DesktopCoverage.Holds(true, true));
    }
}
