using Microsoft.Win32;

namespace Muro.Platform;

/// Start at sign-in, through the per-user Run key (no admin rights, nothing in the system). What it
/// starts is the engine: it runs quietly with the tray icon and plays the wallpaper, and the app is
/// opened from the tray when wanted.
///
/// The Run key belongs to the Windows user, not to a Muro library, so MURO_HOME does not apply to it.
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Written by Task Manager's Startup tab and the Settings app when someone turns an entry off.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static bool IsEnabled(string name = "Muro")
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(name) is not string) return false;
        // A first byte of 02 or 06 means enabled, 03 means the user switched it off in Windows.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(name) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
    }

    /// The command Windows will run, if enabled.
    public static string? Command(string name = "Muro")
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(name) as string;
    }

    public static void Enable(string exePath, string name = "Muro")
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(name, "\"" + exePath + "\"");
        // Forget an earlier "disabled in Task Manager", or turning this on here would change nothing.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static void Disable(string name = "Muro")
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(name, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(name, throwOnMissingValue: false);
    }
}
