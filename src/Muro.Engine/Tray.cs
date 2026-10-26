using System.Diagnostics;
using Muro.Core;
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
        var config = EngineConfig.Load(_root);
        var playing = config.AllDisplays is not null || config.PerDisplay.Count > 0;
        var paused = config.Paused == true;
        var items = new List<TrayIcon.Item> { new("Open Muro", AppLauncher.Open), TrayIcon.Item.Separator };
        if (playing)
        {
            items.Add(new(paused ? "Resume wallpaper" : "Pause wallpaper", () => EngineConfig.Update(_root, c => c.Paused = paused ? null : true)));
            // Wakes a wallpaper that Pause After froze and starts its clock again.
            if (!paused) items.Add(new("Play again", _replay));
        }
        if (_runningSchedule() is { } name)
            items.Add(new($"Stop \"{name}\"", () => ScheduleControl.Stop(_root, Surface.Desktop, restore: true)));
        if (playing || _runningSchedule() is not null) items.Add(TrayIcon.Item.Separator);
        items.Add(new("Quit Muro", quit));
        return items;
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
