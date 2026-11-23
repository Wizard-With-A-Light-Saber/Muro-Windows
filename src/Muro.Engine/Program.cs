using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;
using Muro.Engine;
using Muro.Platform;

// Engine harness. Until the library arrives in M3 a wallpaper id is simply a video file path.
//   Muro.Engine <video> [--monitor N=<video>]... [--efficient] [--play-only-on-desktop]
//               [--no-fullscreen-pause] [--low-battery N] [--seconds N] [--test-pause-after N]
//   Muro.Engine --list-monitors | --dump
//   Muro.Engine            (no video: runs whatever %LOCALAPPDATA%\Muro\config.json assigns)
[System.Runtime.InteropServices.DllImport("kernel32")] static extern bool AttachConsole(int processId);
AttachConsole(-1);   // the exe has no console of its own; this lets the harness switches print to the one that started it
if (args.Contains("--dump")) { Console.WriteLine(DesktopHost.Dump()); return 0; }
if (args.Contains("--list-monitors"))
{
    var i = 1;
    foreach (var m in Monitors.All())
        Console.WriteLine($"{i++}: {m.DeviceName} {m.Bounds.Width}x{m.Bounds.Height}@{m.Bounds.X},{m.Bounds.Y} dpi={m.Dpi} primary={m.Primary}\n   id={m.Id}");
    return 0;
}

// Checks an interactive wallpaper's page without looking at the screen: Muro.Engine --interactive-selftest <index.html>
if (Array.IndexOf(args, "--interactive-selftest") is var selfTest and >= 0 && selfTest + 1 < args.Length) return InteractiveSelfTest.Run(args[selfTest + 1]);

string? Opt(string name) => args.Contains(name) ? args[Array.IndexOf(args, name) + 1] : null;
var positional = args.Where((a, i) => !a.StartsWith("--") && !a.StartsWith("muro:", StringComparison.OrdinalIgnoreCase) && (i == 0 || !new[] { "--seconds", "--low-battery", "--monitor", "--test-pause-after" }.Contains(args[i - 1]))).ToList();
var video = positional.FirstOrDefault();
var perMonitor = args.Select((a, i) => (a, i)).Where(t => t.a == "--monitor").Select(t => args[t.i + 1]).ToList();
var seconds = int.TryParse(Opt("--seconds"), out var s) ? s : 0;
var pauseAfter = int.TryParse(Opt("--test-pause-after"), out var pa) ? pa : 0;

AppPaths.Ensure();
// Run by the uninstaller: put the user's own Windows wallpaper back, then stop.
if (args.Contains("--restore-windows-wallpaper")) { WindowsWallpaperService.RestoreOriginal(AppPaths.Root); return 0; }
// A muro:// link (a Shortcut, a script, another app): carry it out by writing config.json. If no engine is running
// this process then carries on as the engine, so a link works whether Muro is running or not.
if (args.FirstOrDefault(a => a.StartsWith("muro:", StringComparison.OrdinalIgnoreCase)) is { } link) LinkHandler.Run(AppPaths.Root, link);
// One engine per library. The app starts the engine on demand and must never start a second.
using var singleInstance = new Mutex(true, "Muro.Engine." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..16], out var firstEngine);
if (!firstEngine) { Console.Error.WriteLine("Muro engine is already running."); return 0; }
var fromDisk = video is null && perMonitor.Count == 0;
if (fromDisk && Environment.ProcessPath is { } enginePath) { try { UrlProtocol.Register(enginePath); } catch (Exception ex) { Log.Info("muro:// links could not be registered: " + ex.Message); } }
var config = fromDisk ? EngineConfig.Load(AppPaths.Root) : new EngineConfig();
if (!fromDisk)
{
    var mode = args.Contains("--efficient") ? "efficient" : "smooth";
    if (video is not null) config.AllDisplays = new(video, mode);
    var monitors = Monitors.All();
    foreach (var entry in perMonitor)
    {
        var eq = entry.IndexOf('=');
        if (eq < 1 || !int.TryParse(entry[..eq], out var n) || n < 1 || n > monitors.Count) { Console.Error.WriteLine($"bad --monitor {entry}"); return 2; }
        config.PerDisplay[monitors[n - 1].Id] = new(entry[(eq + 1)..], mode);
    }
    if (args.Contains("--play-only-on-desktop")) config.PlayOnlyOnDesktop = true;
    if (args.Contains("--no-fullscreen-pause")) config.PauseOnFullscreen = false;
    if (int.TryParse(Opt("--low-battery"), out var lb)) config.LowBatteryPercent = lb;
}

using var events = new SystemEvents();
Log.Info($"system: {events.State}");
var manager = new WallpaperManager(events, () => config, a => LibraryResolver.Resolve(AppPaths.Root, a));
int cleaned = 0;
ScheduleRunner? runner = null;
var surfaceRunners = new List<ScheduleRunner>();   // the screen saver and lock screen schedules
LockScreenService? lockScreen = null;
AccentService? accent = null;
WindowsWallpaperService? windowsWallpaper = null;
EngineTray? tray = null;
void Cleanup()
{
    if (Interlocked.Exchange(ref cleaned, 1) == 1) return;
    tray?.Dispose();   // removes the icon, or it lingers in the tray until the mouse passes over it
    runner?.Dispose();
    foreach (var other in surfaceRunners) other.Dispose();
    manager.Dispose();
    events.Dispose();
    windowsWallpaper?.RestoreOnExit();   // before the refresh, so the shell repaints the user's own wallpaper
    DesktopHost.RefreshWallpaper();
}

// Crash safety: normal exit, Ctrl+C and unhandled exceptions all restore the desktop. A hard kill
// needs no handler: Windows destroys the process windows and the shell repaints the user wallpaper.
AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Info($"fatal: {e.ExceptionObject}"); Cleanup(); };
events.Error += ex => Log.Info($"error: {ex}");
Console.CancelKeyPress += (_, e) => { e.Cancel = true; MessageLoop.Quit(); };

if (fromDisk)
{
    // Pick up config changes written by the UI. Watcher threads only post work to the loop thread.
    var watcher = new FileSystemWatcher(AppPaths.Root, "config.json") { EnableRaisingEvents = true };
    // The config is saved by writing a temp file and renaming it over config.json, which raises
    // Created or Renamed rather than Changed, so listen to all three.
    // The tray's recent wallpapers: whatever the config now puts on a screen.
    void NoteRecents() { try { AppSettings.NoteRecent(AppPaths.Root, new[] { config.AllDisplays }.Concat(config.PerDisplay.Values).Where(a => a is not null).Select(a => a!.WallpaperID)); } catch (Exception ex) { Log.Info("recents: " + ex.Message); } }
    NoteRecents();
    void Reload() => events.Post(() => { Log.Info("config changed"); config = EngineConfig.Load(AppPaths.Root); NoteRecents(); tray?.Show(config.ShowTrayIcon); lockScreen?.Sync(config); accent?.Sync(config); windowsWallpaper?.Sync(config); manager.Reconcile(); });
    watcher.Changed += (_, _) => Reload();
    watcher.Created += (_, _) => Reload();
    watcher.Renamed += (_, _) => Reload();

    // A wallpaper's own Pause After lives in library.json, so changing it from the Library menu must reach the
    // engine too, or it only takes effect at the next display or config change.
    var libraryWatcher = new FileSystemWatcher(AppPaths.Root, "library.json") { EnableRaisingEvents = true };
    void ReloadLibrary() => events.Post(() => manager.Reconcile());
    libraryWatcher.Changed += (_, _) => ReloadLibrary();
    libraryWatcher.Created += (_, _) => ReloadLibrary();
    libraryWatcher.Renamed += (_, _) => ReloadLibrary();
}

try
{
    manager.Reconcile();
    // Playlists and automations run here, in the engine, so they keep going with the app closed.
    if (fromDisk)
    {
        runner = new ScheduleRunner(AppPaths.Root, events);
        runner.Start();
        foreach (var surface in new[] { Surface.ScreenSaver, Surface.LockScreen })
        {
            var other = new ScheduleRunner(AppPaths.Root, events, surface);
            surfaceRunners.Add(other);
            other.Start();
        }
        lockScreen = new LockScreenService(AppPaths.Root, events);
        lockScreen.Sync(config);
        accent = new AccentService(AppPaths.Root, events);
        accent.Sync(config);
        windowsWallpaper = new WindowsWallpaperService(AppPaths.Root, events);
        windowsWallpaper.Sync(config);
        // Only the real engine has a tray icon, not a harness run started with a video on the command line.
        tray = new EngineTray(AppPaths.Root, () => runner?.RunningName, manager.Replay, MessageLoop.Quit);
        tray.Show(config.ShowTrayIcon);
    }
    using var quitTimer = seconds > 0 ? new Timer(_ => MessageLoop.Quit(), null, seconds * 1000, Timeout.Infinite) : null;
    using var pauseTimer = pauseAfter > 0
        ? new Timer(_ => events.Post(() => { config.Paused = true; manager.Evaluate(); }), null, pauseAfter * 1000, Timeout.Infinite)
        : null;
    MessageLoop.Run();
}
finally { Cleanup(); }
return 0;
