using System.Text.Json.Serialization;

namespace Muro.Core.Library;

/// One wallpaper in the library. Paths are relative to the library root so the whole folder can be
/// moved without touching the manifest; an absolute path is a file Muro does not own.
public sealed class WallpaperEntry
{
    /// Category a video imported by the user is filed under; keeps it out of Explore.
    public const string ImportedCategory = "My Videos";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    /// e.g. "Masters/{id}.mov"
    public string File { get; set; } = "";
    /// Lazily generated 30 fps variant, e.g. "Masters/{id}-eff.mov".
    public string? EfficientFile { get; set; }
    public string? PreviewFile { get; set; }
    /// e.g. "Thumbnails/{id}.jpg"
    public string Thumbnail { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public double Duration { get; set; }
    public long SizeBytes { get; set; }
    public bool Liked { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    /// Per-wallpaper "pause after" override. Null follows the global setting; 0 means never.
    public int? PauseAfterSeconds { get; set; }
    /// The master keeps its audio track (imported while Live Wallpaper Sound was on). Such a wallpaper always plays its
    /// master, since the 30 fps and look copies are silent.
    public bool HasSound { get; set; }
    /// For an import: Fingerprint.Of the source file, so importing it twice is noticed.
    public string? SourceFingerprint { get; set; }
    /// Effects (day/night, mouse, audio) for this wallpaper alone. Null follows the Settings page.
    public EffectsConfig? Effects { get; set; }
    /// A colour look (see <see cref="Media.Looks"/>) baked into the screen-sized copy. Null is the plain wallpaper.
    public string? Look { get; set; }
    /// The look the screen-sized copy was actually made with, so a change of look is noticed and the copy remade.
    public string? BakedLook { get; set; }

    /// <see cref="Look"/>, or null when it is empty or a name this version does not know.
    [JsonIgnore]
    public string? ActiveLook => Media.Looks.Filter(Look) is null ? null : Look;

    /// Every file this wallpaper owns. Deleting a wallpaper deletes exactly these.
    /// An interactive wallpaper: the HTML page the engine draws over the still on the desktop, e.g.
    /// "Hosted Interactive/{id}/index.html". Its <see cref="File"/> is the still picture, which is what plays everywhere
    /// the page cannot (lock screen, screen saver, a paused or covered screen).
    public string? InteractivePage { get; set; }

    [JsonIgnore]
    public bool IsInteractive => !string.IsNullOrEmpty(InteractivePage);

    [JsonIgnore]
    public IEnumerable<string> RelativeFiles =>
        new[] { File, EfficientFile, PreviewFile, Thumbnail, InteractivePage }.Where(f => !string.IsNullOrEmpty(f))!;

    [JsonIgnore]
    public bool IsImported => Category == ImportedCategory;
}
