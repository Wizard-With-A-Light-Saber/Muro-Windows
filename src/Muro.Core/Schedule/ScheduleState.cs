using System.Text.Json;
using System.Text.Json.Serialization;
using Muro.Core.Library;

namespace Muro.Core.Schedule;

/// The three places a wallpaper can play. The desktop runs now; the lock screen and screen saver use the
/// same scheduler and state shape when they arrive, so their schedules need no migration.
public enum Surface { Desktop, LockScreen, ScreenSaver }

/// What one surface schedule is doing, persisted so a restart resumes in the right place.
public sealed class ScheduleState
{
    public string? AutomationID { get; set; }
    public string? PlaylistID { get; set; }
    public int StepIndex { get; set; }
    public DateTime StepStartedAt { get; set; }
    /// Set by the UI or CLI when a schedule is started. The engine starts it from the first step and
    /// clears the flag. Keeps the engine the only thing that applies wallpapers.
    public bool PendingStart { get; set; }
    /// What was on the desktop before the schedule took over, so stopping puts it back.
    public EngineConfig.Assignment? Before { get; set; }

    [JsonIgnore] public bool IsActive => AutomationID is not null || PlaylistID is not null;
}

public static class ScheduleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SecondPrecisionDateConverter() },
    };

    public static string PathIn(string root) => Path.Combine(root, "schedule.json");

    private static string Key(Surface surface) => surface switch
    {
        Surface.LockScreen => "lockScreen",
        Surface.ScreenSaver => "screenSaver",
        _ => "desktop",
    };

    public static Dictionary<string, ScheduleState> LoadAll(string root)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, ScheduleState>>(File.ReadAllText(PathIn(root)), Json) ?? []; }
        catch { return []; }
    }

    public static ScheduleState Load(string root, Surface surface) =>
        LoadAll(root).TryGetValue(Key(surface), out var state) ? state : new ScheduleState();

    public static void Save(string root, Surface surface, ScheduleState state)
    {
        var all = LoadAll(root);
        if (state.IsActive) all[Key(surface)] = state; else all.Remove(Key(surface));
        Directory.CreateDirectory(root);
        SafeFile.WriteAtomically(PathIn(root), JsonSerializer.Serialize(all, Json));
    }
}

/// Starting and stopping schedules from the app and the CLI. Writes intent only; the engine, which is
/// always running, carries it out.
public static class ScheduleControl
{
    public static void StartPlaylist(string root, Surface surface, string playlistId) =>
        Start(root, surface, new ScheduleState { PlaylistID = playlistId });

    public static void StartAutomation(string root, Surface surface, string automationId) =>
        Start(root, surface, new ScheduleState { AutomationID = automationId });

    private static void Start(string root, Surface surface, ScheduleState next)
    {
        var existing = ScheduleStore.Load(root, surface);
        next.PendingStart = true;
        next.StepStartedAt = DateTime.UtcNow;
        // Keep the original "before" when one schedule replaces another.
        next.Before = existing.IsActive ? existing.Before : surface == Surface.Desktop ? EngineConfig.Load(root).AllDisplays : null;
        ScheduleStore.Save(root, surface, next);
    }

    /// Stops the schedule. With restore (the default for the user pressing Stop) the desktop gets back
    /// what it showed before the schedule started; a manual Apply stops without restoring.
    public static void Stop(string root, Surface surface, bool restore = true)
    {
        var existing = ScheduleStore.Load(root, surface);
        if (!existing.IsActive) return;
        ScheduleStore.Save(root, surface, new ScheduleState());
        if (!restore || surface != Surface.Desktop || existing.Before is not { } before) return;
        if (LibraryManifest.Load(root).Find(before.WallpaperID) is null) return;
        var config = EngineConfig.Load(root);
        config.AllDisplays = before;
        config.Save(root);
    }
}
