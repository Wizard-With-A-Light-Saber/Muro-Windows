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

    public static string PathIn(string root) => Path.Combine(root, "settings.json");

    /// A missing or unreadable file gives defaults, never an exception.
    public static AppSettings Load(string root)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathIn(root)), Json) ?? new(); }
        catch { return new(); }
    }

    public static void Update(string root, Action<AppSettings> change)
    {
        var settings = Load(root);
        change(settings);
        Directory.CreateDirectory(root);
        var path = PathIn(root);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
