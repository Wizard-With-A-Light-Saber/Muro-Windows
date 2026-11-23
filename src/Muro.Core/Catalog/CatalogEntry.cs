namespace Muro.Core.Catalog;

/// One wallpaper in the hosted catalog (catalog.json). Same fields as the macOS app reads, so the
/// same published file serves both.
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public double Duration { get; set; }
    public long SizeBytes { get; set; }
    public string Video { get; set; } = "";
    public string Thumbnail { get; set; } = "";
    /// Short 720p loop for the detail view. Absent in older catalogs.
    public string? Preview720 { get; set; }
    /// When it first reached the catalog. Absent in older catalogs.
    public DateTime? PublishedAt { get; set; }

    /// Set for an interactive wallpaper (from interactive.json, not catalog.json): what to download and from where.
    [System.Text.Json.Serialization.JsonIgnore]
    public HostedInteractive.Entry? Interactive { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? InteractiveListUrl { get; set; }
}
