using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muro.Core;

/// Today's Muro's Pick draw: which wallpapers, and on which day (yyyy-MM-dd, local).
public sealed class PickDraw
{
    public List<string> Ids { get; set; } = [];
    public string? Day { get; set; }
}

/// Preferences only the app cares about. What the engine acts on (pause rules, speed, tray) lives in
/// <see cref="EngineConfig"/> so the engine has one file to watch.
public sealed class AppSettings
{
    /// "smooth" or "efficient": how a wallpaper applied with one click plays.
    public string DefaultMode { get; set; } = "smooth";
    /// The release the user has already been told about, so the callout is not shown twice.
    public string? SeenUpdateVersion { get; set; }
    /// The wallpaper chosen for the Featured banner. Falls back when it is no longer in the library.
    public string? HeroId { get; set; }
    public PickDraw Pick { get; set; } = new();

    [JsonIgnore]
    public bool Efficient => DefaultMode == "efficient";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Not settings.json: an older Muro build left a file of that name (other fields, PascalCase) in the
    // same folder, and rewriting it would throw its contents away.
    public static string PathIn(string root) => Path.Combine(root, "app-settings.json");

    /// The file exists but could not be read, so this object holds defaults that must not be saved over it.
    [JsonIgnore]
    public bool Unreadable { get; private set; }

    /// A missing file gives defaults. One that cannot be read is tried again a few times (a virus scanner
    /// may hold it for a moment) and then also gives defaults, never an exception, marked
    /// <see cref="Unreadable"/> so <see cref="Update"/> leaves the real file alone.
    public static AppSettings Load(string root)
    {
        var path = PathIn(root);
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(path)) return new();
            try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new(); }
            catch (Exception)
            {
                if (attempt >= 4) return new() { Unreadable = true };
                Thread.Sleep(50);
            }
        }
    }

    /// Load, change, save. Throws <see cref="IOException"/> rather than overwrite a file it could not read
    /// or lose to a program holding it open; callers treat that as "not saved".
    public static void Update(string root, Action<AppSettings> change)
    {
        var settings = Load(root);
        if (settings.Unreadable)
            throw new IOException(PathIn(root) + " could not be read, so Muro did not overwrite it.");
        change(settings);
        Directory.CreateDirectory(root);
        SafeFile.WriteAtomically(PathIn(root), JsonSerializer.Serialize(settings, Json));
    }
}
