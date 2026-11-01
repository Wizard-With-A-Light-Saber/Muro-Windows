using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Media;

namespace Muro.Core.Tests;

public class EffectsTests
{
    [Theory]
    [InlineData(12, 0)]      // midday
    [InlineData(8, 0)]       // morning ramp finished
    [InlineData(18, 0)]      // evening ramp starts
    [InlineData(21, 1)]      // night
    [InlineData(2, 1)]
    [InlineData(19.5, 0.5)]  // halfway through the evening ramp
    [InlineData(6.5, 0.5)]   // halfway through the morning ramp
    public void Night_FollowsTheClock(double hour, double expected) =>
        Assert.Equal(expected, EffectMath.Night(TimeSpan.FromHours(hour)), 3);

    [Fact] public void DayNight_IsNeutralByDayAndDimmerAtNight()
    {
        Assert.Equal((0, 0), EffectMath.DayNight(TimeSpan.FromHours(13)));
        Assert.Equal((-12, -10), EffectMath.DayNight(TimeSpan.FromHours(23)));
    }

    [Fact] public void Rms_OfSilenceIsZeroAndOfConstantIsTheConstant()
    {
        Assert.Equal(0, EffectMath.Rms(new float[256]));
        Assert.Equal(0.5, EffectMath.Rms(Enumerable.Repeat(0.5f, 256).ToArray()), 5);
        Assert.Equal(0, EffectMath.Rms(ReadOnlySpan<float>.Empty));
    }

    [Fact] public void CursorOn_MapsToMinusOneToOne_AndIgnoresOtherScreens()
    {
        var screen = new Rect(1920, 0, 1920, 1080);
        Assert.Equal((0.0, 0.0), EffectMath.CursorOn(screen, 2880, 540));
        Assert.Equal((-1.0, -1.0), EffectMath.CursorOn(screen, 1920, 0));
        Assert.Equal((0.0, 0.0), EffectMath.CursorOn(screen, 100, 100));   // on the other monitor
    }

    [Fact] public void Effects_AreOffByDefault_AndSurviveTheConfigFile()
    {
        var root = Directory.CreateTempSubdirectory("muro-test").FullName;
        Assert.False(new EngineConfig().Effects.Any);
        var cfg = new EngineConfig();
        cfg.Effects.Audio = true;
        cfg.Effects.ShaderPath = @"C:\shaders\a.glsl";
        cfg.Save(root);
        var back = EngineConfig.Load(root).Effects;
        Assert.True(back.Audio);
        Assert.False(back.Cursor);
        Assert.Equal(@"C:\shaders\a.glsl", back.ShaderPath);
    }

    [Fact] public void Look_IsAddedAfterTheScale_AndUnknownLooksAreIgnored()
    {
        var info = new VideoInfo("hevc", 3840, 2160, 25, 10);
        var args = Transcoder.BuildArgs(new("hevc_nvenc", "", true, "hevc"), "in.mov", "out.mov", info, false, (1920, 1080), Looks.Filter("mono"));
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.Equal("scale=1920:1080:flags=lanczos,hue=s=0", vf);
        Assert.Null(Looks.Filter("no-such-look"));
        Assert.Null(new WallpaperEntry { Look = "no-such-look" }.ActiveLook);
    }

    [Fact] public void ChangingTheLook_MakesTheCopyNeeded_EvenForASmallWallpaper()
    {
        var plain = new WallpaperEntry { Width = 1920, Height = 1080, Fps = 30 };
        Assert.False(EfficientVariant.Needs(plain));
        plain.Look = "warm";
        Assert.True(EfficientVariant.Needs(plain));
        plain.EfficientFile = "Masters/x-eff.mov"; plain.BakedLook = "warm";
        Assert.False(EfficientVariant.Needs(plain));
        plain.Look = null;   // taken off again: the looked copy must go
        Assert.True(EfficientVariant.Needs(plain));
    }

    [Fact] public void ABakedLook_PlaysTheCopyInSmoothMode_AndEffectsOverrideTravelsWithIt()
    {
        var root = Directory.CreateTempSubdirectory("muro-test").FullName;
        Directory.CreateDirectory(Path.Combine(root, "Masters"));
        File.WriteAllText(Path.Combine(root, "Masters", "a.mov"), "x");
        File.WriteAllText(Path.Combine(root, "Masters", "a-eff.mov"), "x");
        var entry = new WallpaperEntry { Id = "a", File = "Masters/a.mov", EfficientFile = "Masters/a-eff.mov", Look = "night", BakedLook = "night", Effects = new EffectsConfig { Audio = true } };
        LibraryWriter.Update(root, m => m.Wallpapers.Add(entry));

        var smooth = LibraryResolver.Resolve(root, new EngineConfig.Assignment("a", "smooth"))!.Value;
        Assert.EndsWith("a-eff.mov", smooth.Path);
        Assert.True(smooth.Effects!.Audio);

        LibraryWriter.Update(root, m => m.Find("a")!.BakedLook = null);   // look chosen but not made yet
        Assert.EndsWith("a.mov", LibraryResolver.Resolve(root, new EngineConfig.Assignment("a", "smooth"))!.Value.Path);
    }

    [Theory]
    [InlineData(440)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void Spectrum_PutsASineInTheRightBand_AndSilenceIsFlat(double hz)
    {
        const int rate = 48000;
        var tone = Enumerable.Range(0, Spectrum.FftSize).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();
        var bands = new float[VizFrame.BandCount];
        Spectrum.Bands(tone, rate, bands);

        // Which band should hold this frequency: the same log spacing the analyser uses.
        var expected = (int)(VizFrame.BandCount * Math.Log(hz / 40.0) / Math.Log(16000.0 / 40.0));
        Assert.InRange(Array.IndexOf(bands, bands.Max()), expected - 1, expected + 1);
        Assert.True(bands.Max() > 0.8f, "a half-scale sine should be loud");

        Spectrum.Bands(new float[Spectrum.FftSize], rate, bands);
        Assert.All(bands, b => Assert.Equal(0f, b));
    }

    [Fact] public void VisualizerStyle_UnknownNamesFallBackToSpectrum_AndItSurvivesTheConfigFile()
    {
        Assert.Equal("spectrum", VisualizerStyles.Normalize("hologram"));
        Assert.Equal("meters", VisualizerStyles.Normalize("meters"));
        var root = Directory.CreateTempSubdirectory("muro-test").FullName;
        var cfg = new EngineConfig();
        Assert.False(cfg.Effects.Any);
        cfg.Effects.Visualizer = "waveform";
        Assert.True(cfg.Effects.Any);
        cfg.Save(root);
        Assert.Equal("waveform", EngineConfig.Load(root).Effects.Visualizer);
    }

    [Fact] public void TheComputedAnyFlag_IsNotWrittenToTheConfigFile()
    {
        var root = Directory.CreateTempSubdirectory("muro-test").FullName;
        var cfg = new EngineConfig();
        cfg.Effects.Audio = true;
        cfg.Save(root);
        Assert.DoesNotContain("\"any\"", File.ReadAllText(EngineConfig.PathIn(root)));
    }

    [Fact] public void OldConfigWithoutEffects_LoadsWithAllOff()
    {
        var root = Directory.CreateTempSubdirectory("muro-test").FullName;
        File.WriteAllText(EngineConfig.PathIn(root), "{ \"showTrayIcon\": true }");
        Assert.False(EngineConfig.Load(root).Effects.Any);
    }
}
