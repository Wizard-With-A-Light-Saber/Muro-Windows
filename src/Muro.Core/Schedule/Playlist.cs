using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muro.Core.Schedule;

/// JSON shared by the schedule files. Same shapes as the macOS app writes (arrays, camelCase keys,
/// "timer" / "clock" modes), so the two apps read each other files.
internal static class ScheduleJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static List<T> LoadList<T>(string path)
    {
        try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options) ?? []; }
        catch { return []; }
    }

    /// Atomic: write a sibling then replace, so a reader never sees half a file.
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// A rotation of wallpapers: apply the next one every IntervalMinutes, in order or shuffled.
public sealed record Playlist
{
    public string Id { get; init; } = Guid.NewGuid().ToString().ToLowerInvariant();
    public string Name { get; init; } = "";
    public List<string> WallpaperIDs { get; init; } = [];
    public int IntervalMinutes { get; init; } = 30;
    public bool Shuffle { get; init; }
}

public static class PlaylistStore
{
    public static string PathIn(string root) => Path.Combine(root, "playlists.json");

    public static List<Playlist> Load(string root) => ScheduleJson.LoadList<Playlist>(PathIn(root));

    public static void Save(string root, IEnumerable<Playlist> playlists) => ScheduleJson.Save(PathIn(root), playlists.ToList());

    /// Strips deleted wallpaper ids out of every playlist. Returns the pruned playlists plus the ids
    /// of the ones that just lost their last wallpaper, so a running one can be stopped instead of
    /// left ticking over an empty list.
    public static (List<Playlist> Playlists, List<string> Emptied) Pruned(IEnumerable<Playlist> playlists, ISet<string> removing)
    {
        var emptied = new List<string>();
        var result = playlists.Select(playlist =>
        {
            var kept = playlist.WallpaperIDs.Where(id => !removing.Contains(id)).ToList();
            if (kept.Count == 0 && playlist.WallpaperIDs.Count > 0) emptied.Add(playlist.Id);
            return kept.Count == playlist.WallpaperIDs.Count ? playlist : playlist with { WallpaperIDs = kept };
        }).ToList();
        return (result, emptied);
    }
}

/// Names the pickers show must be unique, or two playlists cannot be told apart.
public static class Names
{
    /// The first of "New Playlist", "New Playlist 2", "New Playlist 3" that nobody has taken.
    public static string Unique(string baseName, IEnumerable<string> taken)
    {
        var used = taken.Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
        if (!used.Contains(baseName.ToLowerInvariant())) return baseName;
        var n = 2;
        while (used.Contains((baseName + " " + n).ToLowerInvariant())) n++;
        return baseName + " " + n;
    }

    /// Whether a name is already taken, ignoring case, surrounding spaces and the row being edited.
    public static bool IsTaken(string name, IEnumerable<(string Id, string Name)> existing, string? excludingId)
    {
        var wanted = name.Trim();
        if (wanted.Length == 0) return false;
        return existing.Any(e => e.Id != excludingId && string.Equals(e.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }
}
