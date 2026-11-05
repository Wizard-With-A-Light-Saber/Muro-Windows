using System.Text.Json;
using Muro.Core;
using Muro.Core.Media;
using Muro.Platform;
using Registry = Microsoft.Win32.Registry;
using RegistryValueKind = Microsoft.Win32.RegistryValueKind;
using RegistryValueOptions = Microsoft.Win32.RegistryValueOptions;

namespace Muro.Engine;

/// Keeps the Windows wallpaper a still frame of the Muro wallpaper on the main display, so a UAC prompt (drawn by
/// Windows on its own secure desktop, which cannot show Muro's video) shows the right picture instead of the old one.
/// The first time, the user's own wallpaper settings (picture, style, and a slideshow if they use one) are saved; when
/// the setting goes off they are put back. Work runs off the message loop (ffmpeg and the registry both block).
internal sealed class WindowsWallpaperService
{
    private const string DesktopKey = @"Control Panel\Desktop";

    /// Registry values that make up "the Windows wallpaper": the picture and its style, and the slideshow settings
    /// (a null list means every value of the key).
    private static readonly (string Key, string[]? Names)[] Sources =
    [
        (DesktopKey, ["WallPaper", "WallpaperStyle", "TileWallpaper"]),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers", null),
        (@"Control Panel\Personalization\Desktop Slideshow", null),
    ];

    private sealed record Saved(string Key, string Name, RegistryValueKind Kind, string Data);

    private readonly string _root;
    private readonly SystemEvents _events;
    private bool _busy, _again;
    private string? _shown;

    public WindowsWallpaperService(string root, SystemEvents events)
    {
        _root = root;
        _events = events;
        _shown = ReadMarker();
    }

    private string SnapshotPath => SnapshotIn(_root);
    private string MarkerPath => MarkerIn(_root);
    private static string SnapshotIn(string root) => Path.Combine(root, "Stills", "windows-wallpaper-original.json");
    private static string MarkerIn(string root) => Path.Combine(root, "Stills", "windows-wallpaper-active.txt");

    private string? ReadMarker()
    {
        try { return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// Call on the loop thread whenever the config may have changed. Always on: the Windows wallpaper follows the main display.
    public void Sync(EngineConfig config)
    {
        string? wanted = null;
        if (Monitors.All().FirstOrDefault(m => m.Primary)?.Id is { } primary)
            wanted = config.AssignmentFor(primary)?.WallpaperID;
        if (wanted == _shown) return;
        if (_busy) { _again = true; return; }
        _busy = true;
        Task.Run(() => Apply(wanted)).ContinueWith(t =>
        {
            if (t.Exception is { } ex) Log.Info($"windows wallpaper: {ex.GetBaseException().Message}");
            _events.Post(() =>
            {
                _busy = false;
                if (_again) { _again = false; Sync(EngineConfig.Load(_root)); }
            });
        });
    }

    private async Task Apply(string? wanted)
    {
        if (wanted is null)
        {
            RestoreOriginal(_root);
            _shown = null;
            return;
        }

        var ffmpeg = Ffmpeg.Locate();
        if (ffmpeg is null) { Log.Info("windows wallpaper: ffmpeg not found, nothing changed"); return; }
        var still = await Stills.EnsureAsync(ffmpeg, _root, wanted);
        if (still is null) { Log.Info("windows wallpaper: no frame could be made for " + wanted); return; }

        // Keep the user's own settings, once: only while nothing of ours is set.
        if (!File.Exists(SnapshotPath)) Save();
        // Fill, not stretch or tile: the same crop Muro's own wallpaper uses.
        using (var desktop = Registry.CurrentUser.CreateSubKey(DesktopKey, writable: true))
        {
            desktop.SetValue("WallpaperStyle", "10", RegistryValueKind.String);
            desktop.SetValue("TileWallpaper", "0", RegistryValueKind.String);
        }
        DesktopHost.SetWindowsWallpaper(still);
        File.WriteAllText(MarkerPath, wanted);
        _shown = wanted;
        Log.Info("windows wallpaper: showing a still of " + wanted);
    }

    private void Save()
    {
        var saved = new List<Saved>();
        foreach (var (key, names) in Sources)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            if (k is null) continue;
            foreach (var name in names ?? k.GetValueNames())
            {
                if (k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) continue;
                var kind = k.GetValueKind(name);
                var data = value switch { byte[] b => Convert.ToBase64String(b), string[] m => string.Join('\0', m), _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
                saved.Add(new Saved(key, name, kind, data));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
        File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(saved));
        Log.Info($"windows wallpaper: saved {saved.Count} settings");
    }

    /// Puts the user's own Windows wallpaper back, if Muro changed it. Called when Muro has nothing to show, when the
    /// engine quits, and by the uninstaller (which runs the engine with --restore-windows-wallpaper).
    public static void RestoreOriginal(string root)
    {
        if (!File.Exists(SnapshotIn(root))) return;
        var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(SnapshotIn(root))) ?? [];

        // First the picture, so Windows repaints it; that call turns a slideshow off, so the saved settings go back after.
        // For a slideshow the picture on show is CurrentWallpaperPath; WallPaper is only Windows' own copy of it.
        string? Value(string name) => saved.FirstOrDefault(s => s.Name == name)?.Data;
        var picture = Value("BackgroundType") == "2" ? Value("CurrentWallpaperPath") : Value("WallPaper");
        if (!string.IsNullOrEmpty(picture) && File.Exists(picture)) DesktopHost.SetWindowsWallpaper(picture);

        foreach (var s in saved)
        {
            using var k = Registry.CurrentUser.CreateSubKey(s.Key, writable: true);
            object value = s.Kind switch
            {
                RegistryValueKind.DWord => int.Parse(s.Data, System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(s.Data, System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.Binary => Convert.FromBase64String(s.Data),
                RegistryValueKind.MultiString => s.Data.Split('\0'),
                _ => s.Data,
            };
            k.SetValue(s.Name, value, s.Kind);
        }
        File.Delete(SnapshotIn(root));
        File.Delete(MarkerIn(root));
        Log.Info("windows wallpaper: original settings restored");
    }

    /// The engine is quitting: Muro's wallpaper goes, so the user's own Windows wallpaper comes back with it.
    public void RestoreOnExit() => RestoreOriginal(_root);
}
