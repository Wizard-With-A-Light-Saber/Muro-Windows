using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muro.Core;

/// What should play where, plus the pause rules. Written by the UI, read (and watched) by the
/// engine. Field names follow the macOS config.json so the two stay recognisable.
public sealed class EngineConfig
{
    public sealed record Assignment(string WallpaperID, string Mode = "smooth")
    {
        [JsonIgnore]
        public bool IsEfficient => Mode == "efficient";
    }

    /// Fallback for every monitor without a specific assignment.
    public Assignment? AllDisplays { get; set; }
    /// Per-monitor overrides, keyed by the stable monitor id from the platform layer.
    public Dictionary<string, Assignment> PerDisplay { get; set; } = new();

    public bool? Paused { get; set; }
    public double? PlaybackSpeed { get; set; }

    /// Energy section. Fullscreen defaults on (it is what keeps a hidden wallpaper at 0% CPU);
    /// the battery rules default on too, since laptops are the common case on Windows.
    public bool PauseOnFullscreen { get; set; } = true;
    public bool PauseOnBatterySaver { get; set; } = true;
    public bool PauseOnLowBattery { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;
    public bool? PlayOnlyOnDesktop { get; set; }
    public bool? ReplayOnClearDesktop { get; set; }
    public int? PauseAfterSeconds { get; set; }

    /// The engine's notification-area icon. On by default: with the app closed it is the way back in.
    public bool ShowTrayIcon { get; set; } = true;

    /// Live Wallpaper Sound (macOS 6.1): videos imported while this is on keep their sound, and the one applied last plays it.
    public bool LiveSound { get; set; }

    /// The Windows accent colour follows the wallpaper on the main display. The user's own colour is kept and comes back when off.
    public bool MatchAccent { get; set; }

    /// Optional effects over the video (audio, mouse, day/night, stats, shader). All off until the user turns one on.
    public EffectsConfig Effects { get; set; } = new();

    /// What the Muro screen saver plays. Null follows the desktop wallpaper.
    public Assignment? ScreenSaver { get; set; }
    /// The still shown on the lock screen (Windows cannot play video there). Null leaves the lock screen alone.
    public Assignment? LockScreen { get; set; }

    public Assignment? AssignmentFor(string? monitorId) =>
        monitorId is not null && PerDisplay.TryGetValue(monitorId, out var specific) ? specific : AllDisplays;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathIn(string root) => Path.Combine(root, "config.json");

    /// The file exists but could not be read (another program had it open, or it is damaged), so this
    /// object holds defaults and <see cref="Save"/> will not write them over it.
    [JsonIgnore]
    public bool Unreadable { get; private set; }

    /// A missing file gives defaults. One that cannot be read is tried again a few times, since a virus
    /// scanner or the engine's own watcher may hold it for a moment, and then also gives defaults, never
    /// an exception: the engine must keep running on a damaged config. Such a config is marked
    /// <see cref="Unreadable"/> so that nobody saves those defaults over the real file.
    public static EngineConfig Load(string root)
    {
        var path = PathIn(root);
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(path)) return new();
            try { return JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(path), Json) ?? new(); }
            catch (Exception)
            {
                if (attempt >= 4) return new() { Unreadable = true };
                Thread.Sleep(50);
            }
        }
    }

    /// Load, change one thing, save. The schedule runner writes this file too, so a caller must never
    /// hold a config and save it later: that would put back a wallpaper the playlist just rotated away from.
    public static void Update(string root, Action<EngineConfig> change)
    {
        var config = Load(root);
        change(config);
        config.Save(root);
    }

    /// Atomic (see <see cref="SafeFile"/>), and a failure is an <see cref="IOException"/>. Refuses to replace a
    /// file that could not be read.
    public void Save(string root)
    {
        if (Unreadable)
            throw new IOException("config.json could not be read, so Muro did not overwrite it. Close whatever is using it, or delete it to start over.");
        Directory.CreateDirectory(root);
        SafeFile.WriteAtomically(PathIn(root), JsonSerializer.Serialize(this, Json));
    }
}
