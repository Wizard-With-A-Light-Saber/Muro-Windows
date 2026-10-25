namespace Muro.Core;

[Flags]
public enum PauseReason
{
    None = 0,
    User = 1,
    Fullscreen = 2,
    DisplayOff = 4,
    SessionLocked = 8,
    BatterySaver = 16,
    LowBattery = 32,
    DesktopCovered = 64,
    /// Pause After froze this screen on a frame. Added by the engine, which owns the clock.
    PauseAfter = 128,
}

/// What the machine is doing right now, as the platform layer reports it.
public sealed record SystemState
{
    public bool DisplayOn { get; init; } = true;
    public bool SessionLocked { get; init; }
    public bool BatterySaver { get; init; }
    public bool OnBattery { get; init; }
    /// 0-100, or null when there is no battery or Windows does not know.
    public int? BatteryPercent { get; init; }
}

public static class PausePolicy
{
    /// Why a monitor's wallpaper should be paused, or <see cref="PauseReason.None"/> to play.
    /// Display sleep and session lock always pause: nothing can be seen.
    public static PauseReason Decide(
        EngineConfig config, SystemState system, bool monitorFullscreen, bool? monitorCovered)
    {
        var reason = PauseReason.None;
        if (config.Paused == true) reason |= PauseReason.User;
        if (!system.DisplayOn) reason |= PauseReason.DisplayOff;
        if (system.SessionLocked) reason |= PauseReason.SessionLocked;
        if (config.PauseOnFullscreen && monitorFullscreen) reason |= PauseReason.Fullscreen;
        if (config.PauseOnBatterySaver && system.BatterySaver) reason |= PauseReason.BatterySaver;
        if (config.PauseOnLowBattery && system.OnBattery
            && system.BatteryPercent is { } percent && percent < config.LowBatteryPercent)
            reason |= PauseReason.LowBattery;
        if (DesktopCoverage.Holds(monitorCovered, config.PlayOnlyOnDesktop == true)) reason |= PauseReason.DesktopCovered;
        return reason;
    }
}
