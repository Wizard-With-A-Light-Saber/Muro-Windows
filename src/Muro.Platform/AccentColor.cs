using System.Runtime.InteropServices;
using Microsoft.Win32;
using Muro.Core.Accent;

namespace Muro.Platform;

/// What Windows had for its accent colour before Muro changed it, so turning "match the wallpaper" off puts it
/// back exactly. Null means the value did not exist.
public sealed class AccentBackup
{
    public int? AccentColor { get; set; }
    public int? ColorizationColor { get; set; }
    public int? ColorizationAfterglow { get; set; }
    public int? AccentColorMenu { get; set; }
    public int? StartColorMenu { get; set; }
    public string? AccentPalette { get; set; }   // base64 of the 32 bytes
    public int? AutoColorization { get; set; }   // a DWORD: 1 = Windows picks the colour from the still wallpaper
}

/// Sets the Windows accent colour (taskbar, Start, title bars, and every app that follows it, Muro included)
/// the way the Settings app does: the per-user DWM and Explorer\Accent values, then a broadcast so running
/// programs notice. Per-user registry only, no admin rights.
public static class AccentColor
{
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string Desktop = @"Control Panel\Desktop";

    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, string? lParam, uint flags, uint timeout, out nint result);

    private static int? Dword(RegistryKey? key, string name) => key?.GetValue(name) is int v ? v : null;

    public static AccentBackup Read()
    {
        using var dwm = Registry.CurrentUser.OpenSubKey(Dwm);
        using var accent = Registry.CurrentUser.OpenSubKey(Accent);
        using var desktop = Registry.CurrentUser.OpenSubKey(Desktop);
        return new AccentBackup
        {
            AccentColor = Dword(dwm, "AccentColor"),
            ColorizationColor = Dword(dwm, "ColorizationColor"),
            ColorizationAfterglow = Dword(dwm, "ColorizationAfterglow"),
            AccentColorMenu = Dword(accent, "AccentColorMenu"),
            StartColorMenu = Dword(accent, "StartColorMenu"),
            AccentPalette = accent?.GetValue("AccentPalette") is byte[] palette ? Convert.ToBase64String(palette) : null,
            AutoColorization = Dword(desktop, "AutoColorization"),
        };
    }

    // The registry stores these as 0xAABBGGRR (accent) or 0xAARRGGBB (colorization).
    private static int Abgr(Rgb c) => unchecked((int)(0xFF000000u | (uint)c.B << 16 | (uint)c.G << 8 | c.R));
    private static int Argb(Rgb c) => unchecked((int)(0xFF000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B));

    /// Makes <paramref name="accent"/> the Windows accent colour.
    public static void Apply(Rgb accent)
    {
        var palette = AccentPicker.Palette(accent);
        var bytes = new byte[32];
        for (var i = 0; i < 8; i++) { bytes[i * 4] = palette[i].R; bytes[i * 4 + 1] = palette[i].G; bytes[i * 4 + 2] = palette[i].B; bytes[i * 4 + 3] = 0xFF; }

        using (var dwm = Registry.CurrentUser.CreateSubKey(Dwm))
        {
            dwm.SetValue("AccentColor", Abgr(accent), RegistryValueKind.DWord);
            dwm.SetValue("ColorizationColor", Argb(accent), RegistryValueKind.DWord);
            dwm.SetValue("ColorizationAfterglow", Argb(accent), RegistryValueKind.DWord);
        }
        using (var key = Registry.CurrentUser.CreateSubKey(Accent))
        {
            key.SetValue("AccentColorMenu", Abgr(accent), RegistryValueKind.DWord);
            key.SetValue("StartColorMenu", Abgr(palette[4]), RegistryValueKind.DWord);
            key.SetValue("AccentPalette", bytes, RegistryValueKind.Binary);
        }
        // Windows' own "pick from my background" would overwrite this with a colour from the still wallpaper.
        using (var desktop = Registry.CurrentUser.CreateSubKey(Desktop))
            desktop.SetValue("AutoColorization", 0, RegistryValueKind.DWord);
    }

    /// Puts back exactly what was there before.
    public static void Restore(AccentBackup before)
    {
        void Put(string path, string name, object? value, RegistryValueKind kind)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value, kind);
        }
        Put(Dwm, "AccentColor", before.AccentColor, RegistryValueKind.DWord);
        Put(Dwm, "ColorizationColor", before.ColorizationColor, RegistryValueKind.DWord);
        Put(Dwm, "ColorizationAfterglow", before.ColorizationAfterglow, RegistryValueKind.DWord);
        Put(Accent, "AccentColorMenu", before.AccentColorMenu, RegistryValueKind.DWord);
        Put(Accent, "StartColorMenu", before.StartColorMenu, RegistryValueKind.DWord);
        Put(Accent, "AccentPalette", before.AccentPalette is null ? null : Convert.FromBase64String(before.AccentPalette), RegistryValueKind.Binary);
        Put(Desktop, "AutoColorization", before.AutoColorization, RegistryValueKind.DWord);
    }

    /// Tells running programs the colours changed. Can take seconds (it asks every window, and waits for slow ones),
    /// so callers do their own bookkeeping first.
    public static void Broadcast()
    {
        const nint HwndBroadcast = 0xFFFF;
        const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 2;
        SendMessageTimeout(HwndBroadcast, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 3000, out _);
    }
}
