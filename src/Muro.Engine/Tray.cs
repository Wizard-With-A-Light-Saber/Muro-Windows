using System.Diagnostics;
using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;
using Muro.Platform;

namespace Muro.Engine;

/// The engine's notification-area icon: the one thing that is always there, even with the app closed.
/// Every action writes a single field through <see cref="EngineConfig.Update"/> (the schedule runner
/// writes that file too) and the engine's own file watcher picks the change up.
internal sealed class EngineTray : IDisposable
{
    private readonly string _root;
    private readonly Func<string?> _runningSchedule;
    private readonly Action _replay;
    private readonly TrayIcon _icon;

    public EngineTray(string root, Func<string?> runningSchedule, Action replay, Action quit)
    {
        _root = root;
        _runningSchedule = runningSchedule;
        _replay = replay;
        _icon = new TrayIcon("Muro")
        {
            Menu = () => BuildMenu(quit),
        };
        _icon.Clicked += AppLauncher.Open;
        _icon.Error += ex => Log.Info($"tray error: {ex}");
    }

    public void Show(bool show)
    {
        if (show) _icon.Show();
        else _icon.Hide();
    }

    private IReadOnlyList<TrayIcon.Item> BuildMenu(Action quit)
    {
        Loc.Use(AppSettings.Load(_root).Language);   // the language can change while the engine runs
        var config = EngineConfig.Load(_root);
        var playing = config.AllDisplays is not null || config.PerDisplay.Count > 0;
        var paused = config.Paused == true;
        var items = new List<TrayIcon.Item> { new(Loc.T("Open Muro"), AppLauncher.Open), TrayIcon.Item.Separator };
        if (playing)
        {
            items.Add(new(paused ? Loc.T("Resume wallpaper") : Loc.T("Pause wallpaper"), () => EngineConfig.Update(_root, c => c.Paused = paused ? null : true)));
            // Wakes a wallpaper that Pause After froze and starts its clock again.
            if (!paused) items.Add(new(Loc.T("Play again"), _replay));
        }
        if (_runningSchedule() is { } name)
            items.Add(new($"Stop \"{name}\"", () => ScheduleControl.Stop(_root, Surface.Desktop, restore: true)));
        if (playing || _runningSchedule() is not null) items.Add(TrayIcon.Item.Separator);
        AddRecents(items, config);
        items.Add(new(Loc.T("Quit Muro"), quit));
        return items;
    }

    /// The ten most recent wallpapers, for all displays and, when there are several, for each display on its own (the
    /// macOS menu bar has a page per display). Picking one does what picking it in the app does.
    private void AddRecents(List<TrayIcon.Item> items, EngineConfig config)
    {
        var manifest = LibraryManifest.Load(_root);
        var recents = AppSettings.Load(_root).Recents.Select(manifest.Find).Where(e => e is not null).Cast<WallpaperEntry>().ToList();
        if (recents.Count == 0) return;

        TrayIcon.Item Menu(string title, string? monitorId, string? current) => new(title, Children:
            recents.Select(e => new TrayIcon.Item(e.Title, () => LinkHandler.SetWallpaper(_root, e, monitorId, "desktop"), Checked: e.Id == current)).ToList());

        items.Add(Menu(Loc.T("Recent wallpapers"), null, config.AllDisplays?.WallpaperID));
        var monitors = Monitors.All();
        if (monitors.Count > 1)
            for (var n = 0; n < monitors.Count; n++)
                items.Add(Menu(Loc.T("Display") + " " + (n + 1) + (monitors[n].Primary ? " " + Loc.T("(main)") : ""), monitors[n].Id, config.AssignmentFor(monitors[n].Id)?.WallpaperID));
        items.Add(TrayIcon.Item.Separator);
    }

    public void Dispose() => _icon.Dispose();
}

/// Brings the app to the front, or starts it. The app is a separate program: closing it does not stop
/// the wallpaper, which the engine owns.
internal static class AppLauncher
{
    public static void Open()
    {
        try
        {
            // The app's assembly is named Muro, so its process and exe are Muro and Muro.exe.
            var running = Process.GetProcessesByName("Muro").FirstOrDefault(p => p.MainWindowHandle != 0);
            if (running is not null)
            {
                WindowTools.Raise(running.MainWindowHandle);
                return;
            }
            if (Locate() is not { } path) { Log.Info("tray: Muro.exe not found"); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        }
        catch (Exception ex) { Log.Info($"tray: could not open the app: {ex.Message}"); }
    }

    private static string? Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        // Installed: the engine sits in an Engine folder next to the app, or beside it.
        foreach (var candidate in new[] { Path.Combine(baseDir, "..", "Muro.exe"), Path.Combine(baseDir, "Muro.exe") })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        // Development tree: the app's build output under src/Muro.App/bin/Release.
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "Muro.App", "bin", "Release");
            if (!Directory.Exists(bin)) continue;
            var found = Directory.EnumerateFiles(bin, "Muro.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }
}
