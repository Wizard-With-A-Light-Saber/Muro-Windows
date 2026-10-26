using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muro.Core;

/// Today's Muro's Pick draw: which wallpapers, and on which day (yyyy-MM-dd, local).
public sealed class PickDraw
{
    public List<string> Ids { get; set; } = [];
    public string? Day { get; set; }
}

/// What Windows had for its screen saver before Muro took it over, so turning Muro off puts it back.
public sealed class ScreenSaverBackup
{
    public string? Exe { get; set; }
    public bool Active { get; set; }
    public int TimeoutSeconds { get; set; }
}

/// Preferences only the app cares about. What the engine acts on (pause rules, speed, tray) lives in
/// <see cref="EngineConfig"/> so the engine has one file to watch.
public sealed class AppSettings
{
    /// "auto", "smooth" or "efficient": how a wallpaper applied with one click (Featured, the screen saver, a
    /// playlist step) plays. Auto gives the heaviest wallpapers (4K at 50 fps or more) the 30 fps copy and the rest
    /// the original frame rate. See <see cref="PlaybackMode"/>.
    /// Stored as "qualityMode" and only when the user picks one: files from before Automatic existed carry an
    /// old "defaultMode": "smooth" that was a default, never a choice, and is ignored.
    [JsonPropertyName("qualityMode")]
    public string? DefaultMode { get; set; }
    /// The release the user has already been told about, so the callout is not shown twice.
    public string? SeenUpdateVersion { get; set; }
    /// The wallpaper chosen for the Featured banner. Falls back when it is no longer in the library.
    public string? HeroId { get; set; }
    public PickDraw Pick { get; set; } = new();
    /// Set while Muro is the Windows screen saver; null otherwise.
    public ScreenSaverBackup? ScreenSaverBefore { get; set; }

    /// The mode this wallpaper gets from the default setting.
    public string ModeFor(Library.WallpaperEntry? entry) => PlaybackMode.Resolve(DefaultMode, entry);

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
