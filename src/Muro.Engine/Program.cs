using Muro.Core;
using Muro.Engine;
using Muro.Platform;

// Engine harness. Until the library arrives in M3 a wallpaper id is simply a video file path.
//   Muro.Engine <video> [--monitor N=<video>]... [--efficient] [--play-only-on-desktop]
//               [--no-fullscreen-pause] [--low-battery N] [--seconds N] [--test-pause-after N]
//   Muro.Engine --list-monitors | --dump
//   Muro.Engine            (no video: runs whatever %LOCALAPPDATA%\Muro\config.json assigns)
if (args.Contains("--dump")) { Console.WriteLine(DesktopHost.Dump()); return 0; }
if (args.Contains("--list-monitors"))
{
    var i = 1;
    foreach (var m in Monitors.All())
        Console.WriteLine($"{i++}: {m.DeviceName} {m.Bounds.Width}x{m.Bounds.Height}@{m.Bounds.X},{m.Bounds.Y} dpi={m.Dpi} primary={m.Primary}\n   id={m.Id}");
    return 0;
}

string? Opt(string name) => args.Contains(name) ? args[Array.IndexOf(args, name) + 1] : null;
var positional = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !new[] { "--seconds", "--low-battery", "--monitor", "--test-pause-after" }.Contains(args[i - 1]))).ToList();
var video = positional.FirstOrDefault();
var perMonitor = args.Select((a, i) => (a, i)).Where(t => t.a == "--monitor").Select(t => args[t.i + 1]).ToList();
var seconds = int.TryParse(Opt("--seconds"), out var s) ? s : 0;
var pauseAfter = int.TryParse(Opt("--test-pause-after"), out var pa) ? pa : 0;

AppPaths.Ensure();
// One engine per library. The app starts the engine on demand and must never start a second.
using var singleInstance = new Mutex(true, "Muro.Engine." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..16], out var firstEngine);
if (!firstEngine) { Console.Error.WriteLine("Muro engine is already running."); return 0; }
var fromDisk = video is null && perMonitor.Count == 0;
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
void Cleanup()
{
    if (Interlocked.Exchange(ref cleaned, 1) == 1) return;
    runner?.Dispose();
    manager.Dispose();
    events.Dispose();
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
    void Reload() => events.Post(() => { Log.Info("config changed"); config = EngineConfig.Load(AppPaths.Root); manager.Reconcile(); });
    watcher.Changed += (_, _) => Reload();
    watcher.Created += (_, _) => Reload();
    watcher.Renamed += (_, _) => Reload();
}

try
{
    manager.Reconcile();
    // Playlists and automations run here, in the engine, so they keep going with the app closed.
    if (fromDisk)
    {
        runner = new ScheduleRunner(AppPaths.Root, events);
        runner.Start();
    }
    using var quitTimer = seconds > 0 ? new Timer(_ => MessageLoop.Quit(), null, seconds * 1000, Timeout.Infinite) : null;
    using var pauseTimer = pauseAfter > 0
        ? new Timer(_ => events.Post(() => { config.Paused = true; manager.Evaluate(); }), null, pauseAfter * 1000, Timeout.Infinite)
        : null;
    MessageLoop.Run();
}
finally { Cleanup(); }
return 0;
