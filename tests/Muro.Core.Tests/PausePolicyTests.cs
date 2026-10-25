using Muro.Core;

namespace Muro.Core.Tests;

public class PausePolicyTests
{
    private static readonly EngineConfig Cfg = new();

    [Fact] public void NothingWrong_Plays() =>
        Assert.Equal(PauseReason.None, PausePolicy.Decide(Cfg, new SystemState(), false, false));

    [Fact] public void DisplayOffAndLock_AlwaysPause() =>
        Assert.Equal(PauseReason.DisplayOff | PauseReason.SessionLocked,
            PausePolicy.Decide(Cfg, new SystemState { DisplayOn = false, SessionLocked = true }, false, null));

    [Fact] public void Fullscreen_PausesOnlyWhenEnabled()
    {
        Assert.Equal(PauseReason.Fullscreen, PausePolicy.Decide(Cfg, new SystemState(), true, null));
        Assert.Equal(PauseReason.None,
            PausePolicy.Decide(new EngineConfig { PauseOnFullscreen = false }, new SystemState(), true, null));
    }

    [Fact] public void LowBattery_NeedsDischargingAndBelowThreshold()
    {
        Assert.Equal(PauseReason.LowBattery,
            PausePolicy.Decide(Cfg, new SystemState { OnBattery = true, BatteryPercent = 19 }, false, null));
        Assert.Equal(PauseReason.None,
            PausePolicy.Decide(Cfg, new SystemState { OnBattery = true, BatteryPercent = 20 }, false, null));
        Assert.Equal(PauseReason.None,
            PausePolicy.Decide(Cfg, new SystemState { OnBattery = false, BatteryPercent = 5 }, false, null));
        Assert.Equal(PauseReason.None,
            PausePolicy.Decide(Cfg, new SystemState { OnBattery = true, BatteryPercent = null }, false, null));
    }

    [Fact] public void Threshold_IsConfigurable() =>
        Assert.Equal(PauseReason.LowBattery, PausePolicy.Decide(new EngineConfig { LowBatteryPercent = 50 },
            new SystemState { OnBattery = true, BatteryPercent = 40 }, false, null));

    [Fact] public void BatterySaver_Pauses() =>
        Assert.Equal(PauseReason.BatterySaver,
            PausePolicy.Decide(Cfg, new SystemState { BatterySaver = true }, false, null));

    [Fact] public void Covered_PausesOnlyWithPlayOnlyOnDesktop()
    {
        Assert.Equal(PauseReason.None, PausePolicy.Decide(Cfg, new SystemState(), false, true));
        Assert.Equal(PauseReason.DesktopCovered,
            PausePolicy.Decide(new EngineConfig { PlayOnlyOnDesktop = true }, new SystemState(), false, true));
    }

    [Fact] public void UserPause_IsAReason() =>
        Assert.Equal(PauseReason.User, PausePolicy.Decide(new EngineConfig { Paused = true }, new SystemState(), false, false));
}
