using Muro.Core;
using Muro.Platform;
using Muro.Playback;

namespace Muro.Engine;

/// One wallpaper window and player per assigned monitor. Reconciles against the live monitor list
/// and the config (hot-plug, resolution change, assignment change) and applies the pause rules.
/// Everything runs on the message-loop thread.
internal sealed class WallpaperManager : IDisposable
{
    private sealed class Slot
    {
        public required MonitorInfo Monitor;
        public required WallpaperWindow Window;
        public required MpvPlayer Player;
        public required string Video;
        public required string Mode;
        public PauseReason Applied = PauseReason.None;
    }

    private readonly SystemEvents _events;
    private readonly Func<EngineConfig> _config;
    private readonly Func<string, string?> _resolveVideo;
    private readonly Dictionary<string, Slot> _slots = new();
    private DesktopHost.Target? _target;
    private HashSet<string>? _covered;
    private nuint _reconcileTimer, _evaluateTimer, _confirmTimer, _heartbeat;

    public WallpaperManager(SystemEvents events, Func<EngineConfig> config, Func<string, string?> resolveVideo)
    {
        _events = events;
        _config = config;
        _resolveVideo = resolveVideo;
        events.DisplayChanged += () => { Log.Info("display change"); Debounce(ref _reconcileTimer, 500, Reconcile); };
        events.ShellRestarted += () => { RemoveAll(); _target = null; Debounce(ref _reconcileTimer, 1500, Reconcile); };
        events.StateChanged += () => Evaluate();
        events.WindowActivity += () => Debounce(ref _evaluateTimer, 150, () => Evaluate());
        // Safety net for changes no event announces (a window resized by script, a game's full screen).
        _heartbeat = events.StartTimer(3000, () => Evaluate(), repeat: true);
    }

    private void Debounce(ref nuint timer, int ms, Action action)
    {
        _events.StopTimer(timer);
        timer = _events.StartTimer(ms, action);
    }

    /// Creates, moves, retargets or removes slots so they match the monitors and the config.
    public void Reconcile()
    {
        var config = _config();
        // Looked up every time: the shell recreates its wallpaper layer on display changes.
        var target = DesktopHost.Find();
        if (target is null) { Log.Info("desktop host window not found; will retry"); Debounce(ref _reconcileTimer, 2000, Reconcile); return; }
        if (_target is not null && target.Parent != _target.Parent)
        {
            Log.Info("desktop host window changed; rebuilding");
            RemoveAll();
        }
        _target = target;
        foreach (var dead in _slots.Where(kv => !kv.Value.Window.IsAlive).Select(kv => kv.Key).ToList())
        {
            Log.Info($"wallpaper window lost on {dead}; rebuilding");
            Remove(dead);
        }

        var monitors = Monitors.All();
        foreach (var gone in _slots.Keys.Where(id => monitors.All(m => m.Id != id)).ToList())
        {
            Log.Info($"monitor removed: {gone}");
            Remove(gone);
        }

        foreach (var monitor in monitors)
        {
            var assignment = config.AssignmentFor(monitor.Id);
            var video = assignment is null ? null : _resolveVideo(assignment.WallpaperID);
            if (assignment is null || video is null)
            {
                if (_slots.ContainsKey(monitor.Id)) Remove(monitor.Id);
                continue;
            }

            if (_slots.TryGetValue(monitor.Id, out var slot))
            {
                if (slot.Monitor.Bounds != monitor.Bounds)
                {
                    Log.Info($"monitor resized: {monitor.DeviceName} {Describe(monitor.Bounds)}");
                    slot.Window.Move((int)monitor.Bounds.X, (int)monitor.Bounds.Y, (int)monitor.Bounds.Width, (int)monitor.Bounds.Height);
                }
                slot.Monitor = monitor;
                if (slot.Video != video || slot.Mode != assignment.Mode)
                {
                    Log.Info($"{monitor.DeviceName}: now playing {Path.GetFileName(video)} ({assignment.Mode})");
                    Apply(slot, video, assignment.Mode);
                }
                continue;
            }

            try
            {
                var window = new WallpaperWindow(target, (int)monitor.Bounds.X, (int)monitor.Bounds.Y, (int)monitor.Bounds.Width, (int)monitor.Bounds.Height);
                var player = new MpvPlayer(window.Handle, Path.Combine(AppPaths.Logs, "mpv.log"));
                var created = new Slot { Monitor = monitor, Window = window, Player = player, Video = "", Mode = "" };
                Apply(created, video, assignment.Mode);
                _slots[monitor.Id] = created;
                Log.Info($"monitor added: {monitor.DeviceName} {Describe(monitor.Bounds)} dpi={monitor.Dpi} id={monitor.Id}");
            }
            catch (Exception ex)
            {
                Log.Info($"could not start wallpaper on {monitor.DeviceName}: {ex.Message}");
            }
        }
        Evaluate();
    }

    private static void Apply(Slot slot, string video, string mode)
    {
        slot.Player.SetFpsCap(mode == "efficient" ? 30 : 0);
        slot.Player.Load(video);
        slot.Video = video;
        slot.Mode = mode;
    }

    private void Remove(string id)
    {
        if (!_slots.Remove(id, out var slot)) return;
        slot.Player.Dispose();
        slot.Window.Dispose();
    }

    private void RemoveAll()
    {
        foreach (var id in _slots.Keys.ToList()) Remove(id);
        _covered = null;
    }

    /// Decides pause or play for every monitor from the current system state and open windows.
    public void Evaluate(bool confirming = false)
    {
        if (_slots.Count == 0) return;
        // The shell can replace its wallpaper layer a moment after a display change, which takes our
        // windows with it and leaves mpv talking to nothing. Rebuild instead of poking a dead player.
        if (_slots.Values.Any(s => !s.Window.IsAlive))
        {
            Debounce(ref _reconcileTimer, 200, Reconcile);
            return;
        }
        var config = _config();
        var monitors = _slots.Values.Select(s => s.Monitor).ToList();
        var windows = ShellWindows.Enumerate();

        var screens = _slots.ToDictionary(kv => kv.Key, kv => kv.Value.Monitor.Bounds);
        var current = DesktopCoverage.CoveredScreens(windows.Select(w => w.Bounds), screens);
        var (report, needsConfirmation) = DesktopCoverage.Settle(_covered, current, confirming);
        _covered = report;
        if (needsConfirmation) Debounce(ref _confirmTimer, 400, () => Evaluate(confirming: true));

        var fullscreen = ShellWindows.FullscreenMonitors(monitors, windows);
        foreach (var (id, slot) in _slots)
        {
            var reason = PausePolicy.Decide(config, _events.State, fullscreen.Contains(id), report.Contains(id));
            if (reason == slot.Applied) continue;
            slot.Player.Paused = reason != PauseReason.None;
            Log.Info($"{slot.Monitor.DeviceName}: {(reason == PauseReason.None ? "playing" : "paused (" + reason + ")")}");
            slot.Applied = reason;
        }
    }

    private static string Describe(Rect r) => $"{r.Width}x{r.Height}@{r.X},{r.Y}";

    public void Dispose()
    {
        _events.StopTimer(_heartbeat);
        RemoveAll();
    }
}
