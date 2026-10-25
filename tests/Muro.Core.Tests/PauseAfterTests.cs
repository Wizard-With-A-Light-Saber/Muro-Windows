using Muro.Core.Schedule;

namespace Muro.Core.Tests;

/// Ported from the macOS PauseAfterTests. The one rule inside the feature is exactly the kind that breaks quietly.
public class PauseAfterTests
{
    [Fact] public void NoSettingAnywhereMeansKeepPlaying() => Assert.Null(PauseAfter.Resolve(null, null));
    [Fact] public void TheGlobalSettingAppliesToAWallpaperWithoutOneOfItsOwn() => Assert.Equal(30, PauseAfter.Resolve(null, 30));
    [Fact] public void AGlobalSettingOfZeroIsOff() => Assert.Null(PauseAfter.Resolve(null, 0));

    [Fact]
    public void AWallpaperOverridesTheGlobalSetting()
    {
        Assert.Equal(10, PauseAfter.Resolve(10, 300));
        Assert.Equal(3600, PauseAfter.Resolve(3600, 10));
    }

    /// The trap: zero is "never pause", a decision the user made on that wallpaper. It must not read as
    /// "no opinion" and fall through to the global setting.
    [Fact]
    public void AWallpaperCanOptOutWhileTheSettingStillAppliesElsewhere()
    {
        Assert.Null(PauseAfter.Resolve(0, 30));
        Assert.Null(PauseAfter.Resolve(0, 3600));
    }

    [Fact]
    public void AWallpaperCanPauseWhileTheGlobalSettingIsOff()
    {
        Assert.Equal(15, PauseAfter.Resolve(15, 0));
        Assert.Equal(15, PauseAfter.Resolve(15, null));
    }

    [Fact]
    public void NegativesAndZeroCollapseToKeepPlaying()
    {
        Assert.Null(PauseAfter.Normalise(null));
        Assert.Null(PauseAfter.Normalise(0));
        Assert.Null(PauseAfter.Normalise(-1));
        Assert.Null(PauseAfter.Normalise(int.MinValue));
        Assert.Null(PauseAfter.Resolve(-5, 30));
        Assert.Null(PauseAfter.Resolve(null, -5));
    }

    [Fact]
    public void EverySettingsChoiceResolvesToItself()
    {
        foreach (var seconds in new[] { 1, 10, 30, 60, 120, 300, 900, 3600, 86_400 })
            Assert.Equal(seconds, PauseAfter.Resolve(null, seconds));
        Assert.Null(PauseAfter.Resolve(null, 0));   // "Off"
    }

    [Fact]
    public void EveryCatalogRateLandsOnAStandardOne()
    {
        Assert.Equal(60, FrameRate.Standard(59.94));
        Assert.Equal(60, FrameRate.Standard(60.16));
        Assert.Equal(30, FrameRate.Standard(29.97));
        Assert.Equal(30, FrameRate.Standard(28.26));
        Assert.Equal(30, FrameRate.Standard(33.35));
        Assert.Equal(50, FrameRate.Standard(44.82));
        Assert.Equal(72, FrameRate.Standard(66.86));
        Assert.Equal(24, FrameRate.Standard(23.98));
        Assert.Equal(25, FrameRate.Standard(24.71));
        Assert.Equal(0, FrameRate.Standard(0));
        Assert.Equal(0, FrameRate.Standard(double.NaN));
        Assert.Equal(240, FrameRate.Standard(240));
    }
}
