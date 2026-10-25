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
    /// For an import: Fingerprint.Of the source file, so importing it twice is noticed.
    public string? SourceFingerprint { get; set; }

    /// Every file this wallpaper owns. Deleting a wallpaper deletes exactly these.
    [JsonIgnore]
    public IEnumerable<string> RelativeFiles =>
        new[] { File, EfficientFile, PreviewFile, Thumbnail }.Where(f => !string.IsNullOrEmpty(f))!;

    [JsonIgnore]
    public bool IsImported => Category == ImportedCategory;
}
