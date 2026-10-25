using Muro.Core;
using Muro.Platform;
using Muro.Playback;

// M1 harness: Muro.Engine <video> [--seconds N] [--pause-after N]   |   Muro.Engine --dump
if (args.Contains("--dump")) { Console.WriteLine(DesktopHost.Dump()); return 0; }
var video = args.FirstOrDefault(a => !a.StartsWith("--"));
if (video is null || !File.Exists(video)) { Console.Error.WriteLine("usage: Muro.Engine <video> [--seconds N] | --dump"); return 2; }
int pauseAfter = args.Contains("--pause-after") ? int.Parse(args[Array.IndexOf(args, "--pause-after") + 1]) : 0;
int seconds = args.Contains("--seconds") ? int.Parse(args[Array.IndexOf(args, "--seconds") + 1]) : 0;

AppPaths.Ensure();
var target = DesktopHost.Find() ?? throw new InvalidOperationException("desktop host window not found");
Console.WriteLine($"layout={target.Layout} parent=0x{target.Parent:X}");

WallpaperWindow? window = null;
MpvPlayer? player = null;
int cleaned = 0;
void Cleanup()
{
    if (Interlocked.Exchange(ref cleaned, 1) == 1) return;
    player?.Dispose();
    window?.Dispose();
    DesktopHost.RefreshWallpaper();
}

// Crash safety: normal exit, Ctrl+C and unhandled exceptions all restore the desktop.
AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
AppDomain.CurrentDomain.UnhandledException += (_, _) => Cleanup();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; MessageLoop.Quit(); };

try
{
    var (x, y, w, h) = DesktopHost.PrimaryMonitor();
    window = new WallpaperWindow(target, x, y, w, h);
    player = new MpvPlayer(window.Handle, Path.Combine(AppPaths.Logs, "mpv.log"));
    player.Load(video);
    using var quitTimer = seconds > 0 ? new Timer(_ => MessageLoop.Quit(), null, seconds * 1000, Timeout.Infinite) : null;
    using var pauseTimer = pauseAfter > 0 ? new Timer(_ => player.Paused = true, null, pauseAfter * 1000, Timeout.Infinite) : null;
    MessageLoop.Run();
}
finally { Cleanup(); }
return 0;
