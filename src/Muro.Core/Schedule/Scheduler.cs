namespace Muro.Core.Schedule;

/// Runs one automation or one playlist. Pure: it owns no timer and reads time only through the
/// clocks it is given, so it is tested by stepping a fake clock. The host (the engine) owns the one
/// real timer and calls <see cref="Fire"/> at <see cref="NextWakeUtc"/>.
///
/// Absolute deadlines (a UTC time, never a tick count), so nothing drifts and a PC that slept through
/// three steps lands on the wallpaper that should be showing now. Where it is and when the step
/// started are exposed for the host to persist, so quitting mid step resumes in the right place.
///
/// Only one schedule runs at a time per place: starting a playlist stops any automation and the
/// other way round, because both drive the same wallpaper.
public sealed class Scheduler
{
    private readonly Func<DateTime> _utcNow;
    private readonly Func<DateTime> _localNow;
    private readonly Func<int, int> _random;

    public Scheduler(Func<DateTime>? utcNow = null, Func<DateTime>? localNow = null, Func<int, int>? random = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _localNow = localNow ?? (() => DateTime.Now);
        _random = random ?? Random.Shared.Next;
    }

    /// The wallpaper showing now (so a step already on screen is not applied again).
    public Func<string?> CurrentId { get; set; } = () => null;
    /// What the scheduler decided to show. There is exactly one place that sets wallpapers: the host.
    public Action<string> Apply { get; set; } = _ => { };
    /// State changed: the host persists it and re-arms its timer.
    public Action Changed { get; set; } = () => { };

    public IReadOnlyList<Automation> Automations { get; private set; } = [];
    public IReadOnlyList<Playlist> Playlists { get; private set; } = [];

    public string? ActiveAutomationID { get; private set; }
    public string? ActivePlaylistID { get; private set; }
    public int StepIndex { get; private set; }
    public DateTime StepStartedAtUtc { get; private set; }

    public Automation? ActiveAutomation => Automations.FirstOrDefault(a => a.Id == ActiveAutomationID);
    public Playlist? ActivePlaylist => Playlists.FirstOrDefault(p => p.Id == ActivePlaylistID);
    public bool IsRunning => ActiveAutomationID is not null || ActivePlaylistID is not null;
    public string? RunningName => ActiveAutomation?.Name ?? ActivePlaylist?.Name;

    /// Restores what the host persisted. Nothing is applied until <see cref="Reevaluate"/>.
    public void Restore(string? automationId, string? playlistId, int stepIndex, DateTime startedAtUtc)
    {
        ActiveAutomationID = automationId;
        ActivePlaylistID = automationId is null ? playlistId : null;
        StepIndex = Math.Max(0, stepIndex);
        StepStartedAtUtc = startedAtUtc == default ? _utcNow() : startedAtUtc;
    }

    /// Called whenever the schedules change on disk or in the app. A schedule that was edited, emptied
    /// or deleted while running must not keep a stale timer alive.
    public void Update(IReadOnlyList<Automation> automations, IReadOnlyList<Playlist> playlists)
    {
        Automations = automations;
        Playlists = playlists;
        if (ActiveAutomationID is not null && (ActiveAutomation?.Steps.Count ?? 0) == 0) Stop();
        else if (ActivePlaylistID is not null && (ActivePlaylist?.WallpaperIDs.Count ?? 0) == 0) Stop();
        else Changed();
    }

    // ---- Start and stop

    public void StartAutomation(Automation automation)
    {
        if (automation.Steps.Count == 0) return;
        ActivePlaylistID = null;
        ActiveAutomationID = automation.Id;
        StepIndex = 0;
        StepStartedAtUtc = _utcNow();
        ApplyCurrent(automation);
        Changed();
    }

    public void StartPlaylist(Playlist playlist)
    {
        if (playlist.WallpaperIDs.Count == 0) return;
        ActiveAutomationID = null;
        ActivePlaylistID = playlist.Id;
        StepIndex = 0;
        StepStartedAtUtc = _utcNow();
        ApplyId(playlist.WallpaperIDs[0]);
        Changed();
    }

    public void Stop()
    {
        ActiveAutomationID = null;
        ActivePlaylistID = null;
        Changed();
    }

    // ---- Moving through a playlist by hand

    /// Next or previous. Shuffle picks any other wallpaper; in order, a current wallpaper that is not
    /// in the list starts the list from its first (or last) entry.
    public void Advance(bool forward)
    {
        if (ActivePlaylist is not { } playlist || playlist.WallpaperIDs.Count == 0) return;
        var ids = playlist.WallpaperIDs;
        var current = CurrentId();
        string next;
        if (playlist.Shuffle)
        {
            var others = ids.Where(id => id != current).ToList();
            next = others.Count > 0 ? others[_random(others.Count)] : ids[0];
        }
        else
        {
            var at = current is null ? -1 : ids.IndexOf(current);
            next = at < 0 ? (forward ? ids[0] : ids[^1]) : ids[(at + (forward ? 1 : ids.Count - 1)) % ids.Count];
        }
        ApplyId(next);
        StepStartedAtUtc = _utcNow();
        Changed();
    }

    /// Puts a wallpaper that still exists on screen now, after the one showing was deleted: a playlist
    /// moves on to its next one, an automation shows whichever step is due (inside a gap the last window
    /// to end takes over, since a gap has nothing to keep).
    public void ShowCurrentAgain()
    {
        if (ActiveAutomation is { } automation)
        {
            if (automation.Mode == AutomationMode.Timer && StepIndex >= automation.Steps.Count)
            {
                StepIndex = 0;
                StepStartedAtUtc = _utcNow();
            }
            if (automation.Mode == AutomationMode.Clock)
            {
                if (automation.ClockStepOrLast(MinuteOfDay()) is { } step) ApplyId(step.WallpaperID);
            }
            else ApplyCurrent(automation);
            Changed();
        }
        else if (ActivePlaylist is not null) Advance(forward: true);
    }

    // ---- The clock

    /// Works out what should be showing now. Called on a resume from sleep and after an edit, so it is
    /// always driven by absolute time. A playlist advances only when its deadline has actually passed:
    /// re-evaluating for an unrelated reason (a battery update) must never skip an entry.
    public void Reevaluate()
    {
        if (ActiveAutomation is { } automation)
        {
            if (automation.Mode == AutomationMode.Clock) ApplyCurrent(automation);
            else CatchUpTimer(automation);
        }
        else if (ActivePlaylist is { } playlist)
        {
            if (_utcNow() >= PlaylistDeadline(playlist)) Advance(forward: true);
        }
        Changed();
    }

    /// The timer fired. A timer step moves to the next one; everything else is a re-evaluation.
    public void Fire()
    {
        if (ActiveAutomation is { Mode: AutomationMode.Timer } automation)
        {
            StepIndex = (StepIndex + 1) % Math.Max(1, automation.Steps.Count);
            StepStartedAtUtc = _utcNow();
            ApplyCurrent(automation);
            Changed();
        }
        else Reevaluate();
    }

    /// Catch up through however many steps elapsed while asleep, rather than stepping once and being
    /// wrong all day.
    private void CatchUpTimer(Automation automation)
    {
        var elapsed = (_utcNow() - StepStartedAtUtc).TotalSeconds;
        var total = (double)automation.CycleSeconds;
        // Whole cycles are irrelevant, only the remainder matters.
        if (total > 0 && elapsed > total) elapsed %= total;

        var guard = 0;
        while (guard < automation.Steps.Count * 2 && StepAt(automation, StepIndex) is { } step && elapsed >= step.Duration)
        {
            elapsed -= step.Duration;
            StepIndex = (StepIndex + 1) % automation.Steps.Count;
            guard++;
        }
        StepStartedAtUtc = _utcNow().AddSeconds(-Math.Max(0, elapsed));
        ApplyCurrent(automation);
    }

    private void ApplyCurrent(Automation automation)
    {
        switch (automation.Mode)
        {
            case AutomationMode.Timer:
                if (StepAt(automation, StepIndex) is { } step) ApplyId(step.WallpaperID);
                break;
            case AutomationMode.Clock:
                // A gap keeps whatever is playing, which is the documented rule.
                if (automation.ClockStep(MinuteOfDay()) is { } window) ApplyId(window.WallpaperID);
                break;
        }
    }

    private void ApplyId(string id)
    {
        if (id != CurrentId()) Apply(id);
    }

    private static Step? StepAt(Automation automation, int index) =>
        index >= 0 && index < automation.Steps.Count ? automation.Steps[index] : null;

    /// A playlist changes on the clock, as the macOS app does since 6.2: every 30 minutes is :00 and :30, every hour
    /// is on the hour, any other length is counted from local midnight. The first wallpaper may therefore show for
    /// less than a full interval. The count restarts at midnight, so an interval that does not divide the day (7
    /// minutes) still has a boundary at 00:00.
    /// shortcut: the local offset is taken from the two clocks now, so a daylight-saving change inside one interval
    /// lands one hour off for that single change; the next deadline is right again.
    private DateTime PlaylistDeadline(Playlist playlist)
    {
        var minutes = Math.Max(1, playlist.IntervalMinutes);
        var offset = _localNow() - _utcNow();
        var startLocal = StepStartedAtUtc + offset;
        var sinceMidnight = (startLocal - startLocal.Date).TotalMinutes;
        var next = Math.Min(1440, (Math.Floor(sinceMidnight / minutes) + 1) * minutes);
        return startLocal.Date.AddMinutes(next) - offset;
    }

    /// When the host should call <see cref="Fire"/> next: one wake per step, or one per day boundary for
    /// a clock schedule. Null when nothing is running. Never earlier than a second from now.
    public DateTime? NextWakeUtc
    {
        get
        {
            var now = _utcNow();
            DateTime? deadline = null;
            if (ActiveAutomation is { } automation)
            {
                if (automation.Mode == AutomationMode.Timer)
                {
                    if (StepAt(automation, StepIndex) is { } step) deadline = StepStartedAtUtc.AddSeconds(step.Duration);
                }
                else
                {
                    var local = _localNow();
                    deadline = now.AddSeconds(automation.MinutesToNextBoundary(MinuteOfDay()) * 60.0 - local.Second);
                }
            }
            else if (ActivePlaylist is { } playlist) deadline = PlaylistDeadline(playlist);
            if (deadline is null) return null;
            return deadline < now.AddSeconds(1) ? now.AddSeconds(1) : deadline;
        }
    }

    private int MinuteOfDay()
    {
        var local = _localNow();
        return local.Hour * 60 + local.Minute;
    }
}
