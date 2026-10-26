using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;
using Muro.Platform;

namespace Muro.Engine;

/// Runs the desktop schedule (a playlist or an automation) inside the engine, which keeps running when
/// the app is closed. The UI and CLI only write intent (schedule.json, playlists.json, automations.json);
/// this is the one place that applies the wallpapers, so there is a single path and no race.
///
/// The scheduler itself is pure (see Muro.Core). The runner owns the single timer, persists where the
/// schedule is so a restart resumes in the right step, and re-evaluates on a real resume from sleep or a
/// clock change, never on unrelated power events (that would skip playlist entries).
internal sealed class ScheduleRunner : IDisposable
{
    private readonly string _root;
    private readonly SystemEvents _events;
    private readonly Surface _surface;
    private readonly Scheduler _scheduler = new();
    private FileSystemWatcher? _watcher;
    private nuint _timer, _syncTimer;
    private bool _syncing;

    /// One runner per surface (desktop, screen saver, lock screen), each with its own schedule and state.
    public ScheduleRunner(string root, SystemEvents events, Surface surface = Surface.Desktop)
    {
        _root = root;
        _events = events;
        _surface = surface;
        _scheduler.CurrentId = () => CurrentId(EngineConfig.Load(_root));
        _scheduler.Apply = ApplyToSurface;
        _scheduler.Changed = OnChanged;
        events.Resumed += () => _scheduler.Reevaluate();
        events.TimeChanged += () => _scheduler.Reevaluate();
    }

    public string? RunningName => _scheduler.RunningName;

    /// Watches the files the UI and CLI write. Watcher threads only post work to the message loop.
    public void Start()
    {
        _watcher = new FileSystemWatcher(_root, "*.json") { EnableRaisingEvents = true };
        void Changed(object? sender, FileSystemEventArgs e)
        {
            var name = Path.GetFileName(e.FullPath);
            if (name is "playlists.json" or "automations.json" or "schedule.json" or "library.json") RequestSync();
        }
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += (s, e) => Changed(s, e);
        Sync();
    }

    private void RequestSync() => _events.Post(() =>
    {
        _events.StopTimer(_syncTimer);
        _syncTimer = _events.StartTimer(150, Sync);
    });

    /// Brings the scheduler in line with the files. Idempotent, so a burst of file events is harmless.
    public void Sync()
    {
        // Read the persisted state first: updating the lists reports a change, which must not be allowed
        // to overwrite what a restart needs to resume from.
        var state = ScheduleStore.Load(_root, _surface);
        var automations = AutomationStore.Load(_root);
        var playlists = PlaylistStore.Load(_root);

        _syncing = true;
        try
        {
            _scheduler.Update(automations, playlists);

            if (state.PendingStart && state.IsActive)
            {
                if (state.PlaylistID is { } playlistId && playlists.FirstOrDefault(p => p.Id == playlistId) is { } playlist)
                {
                    Log.Info("schedule: starting playlist " + playlist.Name);
                    _scheduler.StartPlaylist(playlist);
                }
                else if (state.AutomationID is { } automationId && automations.FirstOrDefault(a => a.Id == automationId) is { } automation)
                {
                    Log.Info("schedule: starting automation " + automation.Name);
                    _scheduler.StartAutomation(automation);
                }
            }
            else if (state.IsActive)
            {
                if (state.AutomationID != _scheduler.ActiveAutomationID || state.PlaylistID != _scheduler.ActivePlaylistID)
                {
                    // A restart, or the UI switched schedules: resume where the file says.
                    _scheduler.Restore(state.AutomationID, state.PlaylistID, state.StepIndex, state.StepStartedAt);
                    _scheduler.Reevaluate();
                }
            }
            else if (_scheduler.IsRunning)
            {
                Log.Info("schedule: stopped");
                _scheduler.Stop();
            }

            // The wallpaper on screen was deleted, or nothing is on screen: put a valid one up.
            if (_scheduler.IsRunning)
            {
                var current = CurrentId(EngineConfig.Load(_root));
                if (current is null || LibraryManifest.Load(_root).Find(current) is null) _scheduler.ShowCurrentAgain();
            }
        }
        finally { _syncing = false; }

        Persist();
        ArmTimer();
    }

    private void OnChanged()
    {
        if (_syncing) return;
        Persist();
        ArmTimer();
    }

    private string? CurrentId(EngineConfig config) => _surface switch
    {
        Surface.ScreenSaver => config.ScreenSaver?.WallpaperID,
        Surface.LockScreen => config.LockScreen?.WallpaperID,
        _ => config.AllDisplays?.WallpaperID,
    };

    private void ApplyToSurface(string id)
    {
        // Only this surface's assignment: a load-modify-save, so the energy settings the UI wrote are kept.
        // A desktop schedule drives every display, so per-display overrides give way to it.
        var config = EngineConfig.Load(_root);
        switch (_surface)
        {
            case Surface.ScreenSaver:
                config.ScreenSaver = new EngineConfig.Assignment(id, config.ScreenSaver?.Mode ?? "smooth");
                break;
            case Surface.LockScreen:
                config.LockScreen = new EngineConfig.Assignment(id);
                break;
            default:
                config.AllDisplays = new EngineConfig.Assignment(id, config.AllDisplays?.Mode ?? "smooth");
                config.PerDisplay.Clear();
                break;
        }
        config.Save(_root);
        Log.Info($"schedule ({_surface}): now showing {id}");
    }

    /// Writes where the schedule is, keeping what was showing before it started. Skipped when nothing
    /// changed, so the engine does not wake itself up through its own file watcher.
    private void Persist()
    {
        var existing = ScheduleStore.Load(_root, _surface);
        var next = _scheduler.IsRunning
            ? new ScheduleState
            {
                AutomationID = _scheduler.ActiveAutomationID,
                PlaylistID = _scheduler.ActivePlaylistID,
                StepIndex = _scheduler.StepIndex,
                StepStartedAt = ToSecond(_scheduler.StepStartedAtUtc),
                Before = existing.Before,
            }
            : new ScheduleState();

        if (existing.AutomationID == next.AutomationID && existing.PlaylistID == next.PlaylistID
            && existing.StepIndex == next.StepIndex && existing.StepStartedAt == next.StepStartedAt
            && !existing.PendingStart && existing.IsActive == next.IsActive) return;
        ScheduleStore.Save(_root, _surface, next);
    }

    private static DateTime ToSecond(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// One timer, always: one wake per step, or one per day boundary for a clock schedule.
    private void ArmTimer()
    {
        _events.StopTimer(_timer);
        _timer = 0;
        if (_scheduler.NextWakeUtc is not { } wake) return;
        var milliseconds = Math.Clamp((wake - DateTime.UtcNow).TotalMilliseconds, 1000, 2_000_000_000);
        _timer = _events.StartTimer((int)milliseconds, () =>
        {
            _timer = 0;
            _scheduler.Fire();
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _events.StopTimer(_timer);
        _events.StopTimer(_syncTimer);
    }
}
