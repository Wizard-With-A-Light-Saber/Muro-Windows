using System.Runtime.InteropServices;
using Muro.Core;
using Muro.Core.Library;
using Muro.Platform;
using Muro.Playback;

// Muro.scr, the screen saver. Windows starts it with /s (run), /c (configure) or /p <window> (preview).
// It plays the wallpaper chosen for the screen saver (or the desktop's) on every monitor and ends on the first
// key, click, wheel turn or real mouse movement.
[DllImport("user32", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint owner, string text, string caption, uint type);

var mode = (args.FirstOrDefault() ?? "/s").ToLowerInvariant().TrimStart('/', '-');
if (mode.StartsWith('c'))
{
    MessageBoxW(0, "Muro's screen saver is set up in Muro: open Settings, then Screen saver.", "Muro", 0x40);
    return 0;
}
if (mode.StartsWith('p')) return 0;   // the little preview in Windows' dialog is left blank

// Windows may start it again while it runs (a second display wake, a test): one is enough.
using var once = new Mutex(true, "Muro.ScreenSaver", out var first);
if (!first) return 0;

AppPaths.Ensure();
Log("started " + string.Join(' ', args));
try { return Run(); }
catch (Exception ex)
{
    Log(ex.ToString());
    return 1;
}

static void Log(string line)
{
    try { File.AppendAllText(Path.Combine(AppPaths.Logs, "screensaver.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
}

static int Run()
{
    var root = AppPaths.Root;
    var config = EngineConfig.Load(root);
    // Its own wallpaper, else the desktop's, else the first one in the library.
    var assignment = config.ScreenSaver ?? config.AllDisplays;
    if (assignment is null && LibraryManifest.Load(root).Wallpapers.FirstOrDefault() is { } any)
        assignment = new EngineConfig.Assignment(any.Id);
    if (assignment is null || LibraryResolver.Resolve(root, assignment) is not { } video) return 0;   // nothing to show: end at once

    var windows = new List<SaverWindow>();
    var players = new List<MpvPlayer>();
    try
    {
        foreach (var monitor in Monitors.All())
        {
            var window = new SaverWindow((int)monitor.Bounds.X, (int)monitor.Bounds.Y, (int)monitor.Bounds.Width, (int)monitor.Bounds.Height);
            windows.Add(window);
            var player = new MpvPlayer(window.Handle);
            players.Add(player);
            player.HideCursor();
            player.Speed = Math.Clamp(config.PlaybackSpeed ?? 1.0, 0.25, 4.0);
            player.SetFpsCap(assignment.IsEfficient && !video.IsEfficientFile ? 30 : 0);
            player.Load(video.Path);
        }
        using var input = new IdleInputWatcher();
        // Without the hooks nothing could end it, so do not start.
        if (!input.Installed) throw new InvalidOperationException("input hooks were refused: error " + Marshal.GetLastWin32Error());
        input.Activity += () => { Log("input, ending"); MessageLoop.Quit(); };
        MessageLoop.Run();
    }
    finally
    {
        foreach (var player in players) player.Dispose();
        foreach (var window in windows) window.Dispose();
    }
    return 0;
}
