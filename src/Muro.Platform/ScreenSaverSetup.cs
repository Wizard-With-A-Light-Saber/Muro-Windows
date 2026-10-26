using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Muro.Platform;

/// Windows' own per-user screen saver settings (HKCU\Control Panel\Desktop): which program, whether it is on,
/// and the idle time. Pointing SCRNSAVE.EXE at Muro.scr needs no admin rights and nothing in the system.
public static class ScreenSaverSetup
{
    /// What Windows holds: the program (null for none), whether the screen saver is on, and the delay in seconds.
    public sealed record State(string? Exe, bool Active, int TimeoutSeconds);

    private const string DesktopKey = @"Control Panel\Desktop";
    private const uint SPI_SETSCREENSAVETIMEOUT = 0x000F, SPI_SETSCREENSAVEACTIVE = 0x0011, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

    [DllImport("user32", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, nint pv, uint flags);

    public static State Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
        var exe = key?.GetValue("SCRNSAVE.EXE") as string;
        var timeout = int.TryParse(key?.GetValue("ScreenSaveTimeOut") as string, out var seconds) ? seconds : 0;
        return new State(string.IsNullOrEmpty(exe) ? null : exe, key?.GetValue("ScreenSaveActive") as string == "1", timeout);
    }

    /// True when Windows is set to use a program called Muro.scr.
    public static bool IsMuro() =>
        Read().Exe is { } exe && string.Equals(Path.GetFileName(exe), "Muro.scr", StringComparison.OrdinalIgnoreCase);

    /// Makes Muro the screen saver, starting after <paramref name="seconds"/> of no input (0 means never).
    public static void Use(string scrPath, int seconds)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
            key.SetValue("SCRNSAVE.EXE", scrPath, RegistryValueKind.String);
        SetDelay(seconds);
    }

    public static void SetDelay(int seconds)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
        {
            if (seconds > 0) key.SetValue("ScreenSaveTimeOut", seconds.ToString(), RegistryValueKind.String);
            key.SetValue("ScreenSaveActive", seconds > 0 ? "1" : "0", RegistryValueKind.String);
        }
        // Tell the running session, or it keeps the old values until the next sign-in.
        if (seconds > 0) SystemParametersInfo(SPI_SETSCREENSAVETIMEOUT, (uint)seconds, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, seconds > 0 ? 1u : 0u, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    /// Puts back what was there before Muro took the screen saver over.
    public static void Restore(State before)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
        {
            if (before.Exe is null) key.DeleteValue("SCRNSAVE.EXE", throwOnMissingValue: false);
            else key.SetValue("SCRNSAVE.EXE", before.Exe, RegistryValueKind.String);
        }
        SetDelay(before.Active ? Math.Max(1, before.TimeoutSeconds) : 0);
        if (before.TimeoutSeconds > 0)
        {
            using var key = Registry.CurrentUser.CreateSubKey(DesktopKey);
            key.SetValue("ScreenSaveTimeOut", before.TimeoutSeconds.ToString(), RegistryValueKind.String);
        }
    }
}
