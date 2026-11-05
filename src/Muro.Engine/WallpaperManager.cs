using System.Diagnostics;
using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;
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
        /// The user shader this player was started with. mpv takes it at start-up, so a change rebuilds the player.
        public string? Shader;
        /// This wallpaper's own effects; null follows the Settings page.
        public EffectsConfig? Effects;
        public PauseReason Applied = PauseReason.None;
        /// Every reason to hold except Pause After. Transitions are read from this, never from the final
        /// reason, or the heartbeat would re-arm the clock it just fired.
        public PauseReason Holds = PauseReason.None;
        public bool Settled;
        public nuint SettleTimer;
        public int? WallpaperPauseAfter;
        public bool? Covered;
        // The crossfade: a still of the outgoing frame, held above the new video, then faded away.
        public FadeOverlay? Overlay;
        public nuint FadeTimer;
        public Stopwatch? FadeClock;
        public bool Fading;
    }

    /// Short on purpose: long enough not to read as a cut, short enough that the old picture is rarely
    /// kept for long. The same length the macOS app uses.
    private const double FadeMilliseconds = 600;

    private readonly SystemEvents _events;
    private readonly Func<EngineConfig> _config;
    private readonly Func<EngineConfig.Assignment, LibraryResolver.Resolved?> _resolve;
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly EffectsRunner _fx;
    private DesktopHost.Target? _target;
    private HashSet<string>? _covered;
    private nuint _reconcileTimer, _evaluateTimer, _confirmTimer, _retryTimer, _heartbeat;
    private bool _fastHeartbeat;
    private int? _lastGlobalPauseAfter;
    private string _lastFullscreen = "";

    public WallpaperManager(SystemEvents events, Func<EngineConfig> config, Func<EngineConfig.Assignment, LibraryResolver.Resolved?> resolve)
    {
        _events = events;
        _config = config;
        _resolve = resolve;
        _fx = new EffectsRunner(events, config, () => _slots.Values
            .Select(s => new EffectsRunner.Target(s.Player, s.Monitor.Bounds, s.Applied != PauseReason.None, _target, s.Effects ?? config().Effects)).ToList());
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

        var missing = false;
        foreach (var monitor in monitors)
        {
            var assignment = config.AssignmentFor(monitor.Id);
            var resolved = assignment is null ? null : _resolve(assignment);
            if (assignment is not null && resolved is null) missing = true;
            if (assignment is null || resolved is not { } video)
            {
                if (_slots.ContainsKey(monitor.Id)) Remove(monitor.Id);
                continue;
            }

            var shader = config.Effects.Shader && File.Exists(config.Effects.ShaderPath) ? config.Effects.ShaderPath : null;
            if (_slots.TryGetValue(monitor.Id, out var started) && started.Shader != shader)
            {
                Log.Info($"{monitor.DeviceName}: shader changed; rebuilding");
                Remove(monitor.Id);
            }

            if (_slots.TryGetValue(monitor.Id, out var slot))
            {
                if (slot.Monitor.Bounds != monitor.Bounds)
                {
                    Log.Info($"monitor resized: {monitor.DeviceName} {Describe(monitor.Bounds)}");
                    slot.Window.Move((int)monitor.Bounds.X, (int)monitor.Bounds.Y, (int)monitor.Bounds.Width, (int)monitor.Bounds.Height);
                }
                slot.Monitor = monitor;
                slot.Effects = video.Effects;
                if (slot.WallpaperPauseAfter != video.PauseAfterSeconds)
                {
                    slot.WallpaperPauseAfter = video.PauseAfterSeconds;
                    Arm(slot, evaluate: false);
                }
                if (slot.Video != video.Path || slot.Mode != assignment.Mode)
                {
                    Log.Info($"{monitor.DeviceName}: now playing {Path.GetFileName(video.Path)} ({assignment.Mode})");
                    ChangeVideo(slot, video, assignment.Mode);
                }
                continue;
            }

            try
            {
                var window = new WallpaperWindow(target, (int)monitor.Bounds.X, (int)monitor.Bounds.Y, (int)monitor.Bounds.Width, (int)monitor.Bounds.Height);
                var player = new MpvPlayer(window.Handle, Path.Combine(AppPaths.Logs, "mpv.log"), shader: shader);
                var created = new Slot { Monitor = monitor, Window = window, Player = player, Video = "", Mode = "", Shader = shader, Effects = video.Effects };
                Apply(created, video, assignment.Mode);
                _slots[monitor.Id] = created;
                Log.Info($"monitor added: {monitor.DeviceName} {Describe(monitor.Bounds)} dpi={monitor.Dpi} id={monitor.Id}");
            }
            catch (Exception ex)
            {
                Log.Info($"could not start wallpaper on {monitor.DeviceName}: {ex.Message}");
            }
        }
        // A wallpaper that cannot be found is usually a drive that is not connected. Look again later
        // so it comes back by itself once the drive is plugged in.
        if (missing) Debounce(ref _retryTimer, 15000, Reconcile);
        // A new global Pause After value starts every clock again.
        if (config.PauseAfterSeconds != _lastGlobalPauseAfter)
        {
            _lastGlobalPauseAfter = config.PauseAfterSeconds;
            foreach (var slot in _slots.Values) Arm(slot, evaluate: false);
        }
        var speed = Math.Clamp(config.PlaybackSpeed ?? 1.0, 0.25, 4.0);
        foreach (var slot in _slots.Values)
        {
            if (Math.Abs(slot.Player.Speed - speed) < 0.001) continue;
            slot.Player.Speed = speed;
            Log.Info($"{slot.Monitor.DeviceName}: speed {speed}x");
        }
        Evaluate();
    }

    private void Apply(Slot slot, LibraryResolver.Resolved video, string mode)
    {
        // The 30 fps variant needs no filter; otherwise Efficient falls back to dropping frames at the player.
        slot.Player.SetFpsCap(mode == "efficient" && !video.IsEfficientFile ? 30 : 0);
        slot.Player.Load(video.Path);
        slot.Video = video.Path;
        slot.Mode = mode;
        // A new wallpaper is a new chance to watch it move, including every playlist and automation step.
        slot.WallpaperPauseAfter = video.PauseAfterSeconds;
        Arm(slot, evaluate: false);
    }

    /// Switches a screen to another wallpaper without a cut: the outgoing frame is kept on screen as a still
    /// until the new video has a frame of its own, then dissolves. Without a still (nothing rendered yet) it
    /// is a plain switch, which never shows black either, since the old frame stays until the new one is ready.
    private void ChangeVideo(Slot slot, LibraryResolver.Resolved video, string mode)
    {
        EndFade(slot);
        FadeOverlay? overlay = null;
        var still = Path.Combine(Path.GetTempPath(), "muro-fade-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            if (_target is { } target && slot.Player.TryScreenshot(still))
            {
                var b = slot.Monitor.Bounds;
                overlay = new FadeOverlay(target, (int)b.X, (int)b.Y, (int)b.Width, (int)b.Height, still);
            }
        }
        catch (Exception ex) { Log.Info("crossfade unavailable: " + ex.Message); }
        finally { try { File.Delete(still); } catch { } }

        Apply(slot, video, mode);
        if (overlay is null) return;
        slot.Overlay = overlay;
        slot.Fading = false;
        slot.FadeClock = Stopwatch.StartNew();
        slot.FadeTimer = _events.StartTimer(30, () => StepFade(slot, video.Path), repeat: true);
    }

    private void StepFade(Slot slot, string path)
    {
        if (slot.Overlay is null || slot.FadeClock is null) { EndFade(slot); return; }
        var elapsed = slot.FadeClock.ElapsedMilliseconds;
        if (!slot.Fading)
        {
            // Hold the old picture until the new video has a frame up. Give up waiting after three seconds.
            if (!slot.Player.IsShowing(path) && elapsed < 3000) return;
            slot.Fading = true;
            slot.FadeClock.Restart();
            return;
        }
        var t = Math.Min(1.0, elapsed / FadeMilliseconds);
        var eased = t * t * (3 - 2 * t);
        slot.Overlay.SetAlpha((byte)Math.Round(255 * (1 - eased)));
        if (t >= 1) EndFade(slot);
    }

    private void EndFade(Slot slot)
    {
        _events.StopTimer(slot.FadeTimer);
        slot.FadeTimer = 0;
        slot.Overlay?.Dispose();
        slot.Overlay = null;
        slot.Fading = false;
    }

    private void Remove(string id)
    {
        if (!_slots.Remove(id, out var slot)) return;
        EndFade(slot);
        _events.StopTimer(slot.SettleTimer);
        Log.Info("removing wallpaper on " + slot.Monitor.DeviceName);
        slot.Player.Dispose();
        slot.Window.Dispose();
        Log.Info("removed");
    }

    private void RemoveAll()
    {
        foreach (var id in _slots.Keys.ToList()) Remove(id);
        _covered = null;
    }

    /// Decides pause or play for every monitor from the current system state and open windows.
    public void Evaluate(bool confirming = false)
    {
        if (_slots.Count == 0) { _fx.Sync(); return; }
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
        var why = string.Join("; ", fullscreen.Select(kv => (_slots.TryGetValue(kv.Key, out var s) ? s.Monitor.DeviceName : kv.Key) + ": " + kv.Value));
        if (why != _lastFullscreen)
        {
            _lastFullscreen = why;
            Log.Info("full screen: " + (why.Length == 0 ? "none" : why));
        }
        foreach (var (id, slot) in _slots)
        {
            var covered = report.Contains(id);
            var holds = PausePolicy.Decide(config, _events.State, fullscreen.ContainsKey(id), covered);

            // Pause After counts again whenever playback genuinely begins again: it resumes from any other
            // hold (unlock, display wake, desktop clearing under Play only on desktop), or, with Replay on
            // Clear Desktop, the desktop clears even when nothing was holding it.
            var resumed = slot.Holds != PauseReason.None && holds == PauseReason.None;
            var cleared = DesktopCoverage.RestartsPauseAfter(slot.Covered, covered, config.ReplayOnClearDesktop == true);
            slot.Holds = holds;
            slot.Covered = covered;
            if (resumed || cleared) Arm(slot, evaluate: false);

            var reason = holds | (slot.Settled ? PauseReason.PauseAfter : PauseReason.None);
            if (reason == slot.Applied) continue;
            slot.Player.Paused = reason != PauseReason.None;
            Log.Info(slot.Monitor.DeviceName + ": " + (reason == PauseReason.None ? "playing" : "paused (" + reason + ")"));
            slot.Applied = reason;
        }
        // Closing a full-screen or covering window sends no event we hear, so the only thing that ends the hold is the
        // next look. Every 3 s left a frozen or blank desktop on screen after closing a media player; while a screen is
        // held for such a window, look every half second instead.
        var fast = _slots.Values.Any(s => (s.Holds & (PauseReason.Fullscreen | PauseReason.DesktopCovered)) != 0);
        if (fast != _fastHeartbeat)
        {
            _fastHeartbeat = fast;
            _events.StopTimer(_heartbeat);
            _heartbeat = _events.StartTimer(fast ? 500 : 3000, () => Evaluate(), repeat: true);
        }
        _fx.Sync();   // effects follow the pause state and the config; this is also what runs after every config change
    }

    /// Starts this screen Pause After clock again: it plays for the resolved time, then holds on a frame.
    /// The per-wallpaper value wins over the global one, and zero on a wallpaper means never.
    private void Arm(Slot slot, bool evaluate)
    {
        _events.StopTimer(slot.SettleTimer);
        slot.SettleTimer = 0;
        var wasSettled = slot.Settled;
        slot.Settled = false;
        if (PauseAfter.Resolve(slot.WallpaperPauseAfter, _config().PauseAfterSeconds) is { } seconds && slot.Holds == PauseReason.None)
        {
            slot.SettleTimer = _events.StartTimer(seconds * 1000, () =>
            {
                slot.SettleTimer = 0;
                slot.Settled = true;
                Evaluate();
            });
        }
        if (wasSettled && evaluate) Evaluate();
    }

    /// Plays every screen again and starts its Pause After clock over: the tray play button while the
    /// wallpapers rest.
    public void Replay()
    {
        foreach (var slot in _slots.Values) Arm(slot, evaluate: false);
        Evaluate();
    }

    private static string Describe(Rect r) => $"{r.Width}x{r.Height}@{r.X},{r.Y}";

    public void Dispose()
    {
        _events.StopTimer(_heartbeat);
        _fx.Dispose();
        RemoveAll();
    }
}
