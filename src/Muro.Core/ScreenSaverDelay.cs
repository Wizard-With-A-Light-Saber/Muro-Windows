namespace Muro.Core;

/// How long Windows waits for input before the screen saver starts. This is Windows' own per-user setting
/// (HKCU\Control Panel\Desktop, ScreenSaveTimeOut, in seconds), not one of Muro's, so it is read and
/// written there and the Settings row shows what Windows holds.
public static class ScreenSaverDelay
{
    /// Zero means the screen saver never starts.
    public const int Never = 0;

    /// What the Settings list offers; any other value found in the registry is still shown.
    public static readonly int[] Choices = [60, 120, 180, 300, 600, 900, 1800, 3600, 7200, Never];

    public static string Label(int seconds) => seconds switch
    {
        <= 0 => "Never",
        < 3600 when seconds % 60 == 0 => seconds / 60 == 1 ? "1 minute" : seconds / 60 + " minutes",
        < 60 => seconds + " seconds",
        _ when seconds % 3600 == 0 => seconds / 3600 == 1 ? "1 hour" : seconds / 3600 + " hours",
        _ => (seconds / 60) + " minutes",
    };
}
