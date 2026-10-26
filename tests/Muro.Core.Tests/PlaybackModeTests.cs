using Muro.Core.Library;

namespace Muro.Core.Tests;

public class PlaybackModeTests
{
    private static WallpaperEntry Entry(int width, int height, double fps) => new() { Id = "w", Width = width, Height = height, Fps = fps };

    [Theory]
    [InlineData(3840, 2160, 60, true)]     // 4K60: heavy
    [InlineData(3840, 2160, 59.94, true)]  // a common NTSC rate
    [InlineData(3840, 2160, 50, true)]
    [InlineData(5120, 2880, 60, true)]     // above 4K
    [InlineData(3840, 2160, 30, false)]    // 4K30 stays smooth
    [InlineData(3840, 2160, 24, false)]
    [InlineData(2560, 1440, 60, false)]    // 1440p60 stays smooth
    [InlineData(1920, 1080, 60, false)]
    [InlineData(1920, 1080, 120, false)]
    public void OnlyFourKAtHighFrameRateIsHeavy(int width, int height, double fps, bool heavy) =>
        Assert.Equal(heavy, PlaybackMode.IsHeavy(width, height, fps));

    [Fact]
    public void Auto_PicksEfficientOnlyForHeavyWallpapers()
    {
        Assert.Equal("efficient", PlaybackMode.Resolve("auto", Entry(3840, 2160, 60)));
        Assert.Equal("smooth", PlaybackMode.Resolve("auto", Entry(3840, 2160, 30)));
        Assert.Equal("smooth", PlaybackMode.Resolve("auto", Entry(1920, 1080, 60)));
        Assert.Equal("smooth", PlaybackMode.Resolve("auto", null));   // nothing known: do not guess
    }

    [Fact]
    public void AnExplicitChoiceAlwaysWins_AndUnknownTextMeansAuto()
    {
        var heavy = Entry(3840, 2160, 60);
        var light = Entry(1920, 1080, 60);
        Assert.Equal("smooth", PlaybackMode.Resolve("smooth", heavy));
        Assert.Equal("efficient", PlaybackMode.Resolve("efficient", light));
        Assert.Equal("efficient", PlaybackMode.Resolve("nonsense", heavy));
        Assert.Equal("smooth", PlaybackMode.Resolve(null, light));
    }

    [Fact]
    public void AnOldFileWithTheOldSmoothDefault_StillGetsAutomatic()
    {
        using var lib = new TempLibrary();
        // What earlier builds wrote the first time any setting changed: a default, not a choice.
        File.WriteAllText(AppSettings.PathIn(lib.Root), "{ \"defaultMode\": \"smooth\", \"heroId\": \"abc\" }");
        var settings = AppSettings.Load(lib.Root);
        Assert.Equal("abc", settings.HeroId);
        Assert.Equal("efficient", settings.ModeFor(Entry(3840, 2160, 60)));
        // A new explicit choice is stored under the new name and wins.
        AppSettings.Update(lib.Root, s => s.DefaultMode = "smooth");
        Assert.Contains("\"qualityMode\": \"smooth\"", File.ReadAllText(AppSettings.PathIn(lib.Root)));
        Assert.Equal("smooth", AppSettings.Load(lib.Root).ModeFor(Entry(3840, 2160, 60)));
    }

    [Fact]
    public void TheDefaultSetting_IsAuto_SoANewInstallGetsTheRule()
    {
        using var lib = new TempLibrary();
        var settings = AppSettings.Load(lib.Root);
        Assert.Equal("efficient", settings.ModeFor(Entry(3840, 2160, 60)));
        Assert.Equal("smooth", settings.ModeFor(Entry(1920, 1080, 60)));
        AppSettings.Update(lib.Root, s => s.DefaultMode = "smooth");
        Assert.Equal("smooth", AppSettings.Load(lib.Root).ModeFor(Entry(3840, 2160, 60)));
    }
}
