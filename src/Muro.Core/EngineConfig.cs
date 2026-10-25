using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muro.Core;

/// What should play where, plus the pause rules. Written by the UI, read (and watched) by the
/// engine. Field names follow the macOS config.json so the two stay recognisable.
public sealed class EngineConfig
{
    public sealed record Assignment(string WallpaperID, string Mode = "smooth")
    {
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

    public Assignment? AssignmentFor(string? monitorId) =>
        monitorId is not null && PerDisplay.TryGetValue(monitorId, out var specific) ? specific : AllDisplays;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathIn(string root) => Path.Combine(root, "config.json");

    /// A missing or unreadable file gives defaults, never an exception: the engine must keep
    /// running on a damaged config.
    public static EngineConfig Load(string root)
    {
        try { return JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(PathIn(root)), Json) ?? new(); }
        catch { return new(); }
    }

    /// Atomic: write a sibling then replace, so a reader never sees half a file.
    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = PathIn(root);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
