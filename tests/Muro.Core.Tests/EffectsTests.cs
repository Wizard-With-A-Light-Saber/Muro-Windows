using Muro.Core;

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

    [Fact] public void OldConfigWithoutEffects_LoadsWithAllOff()
    {
        var root = Directory.CreateTempSubdirectory("muro-test").FullName;
        File.WriteAllText(EngineConfig.PathIn(root), "{ \"showTrayIcon\": true }");
        Assert.False(EngineConfig.Load(root).Effects.Any);
    }
}
