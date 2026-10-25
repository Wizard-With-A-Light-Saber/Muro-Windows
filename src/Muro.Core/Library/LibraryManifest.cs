using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muro.Core.Library;

/// library.json: the list of wallpapers the user has. Same shape as the macOS manifest.
public sealed class LibraryManifest
{
    public List<WallpaperEntry> Wallpapers { get; set; } = [];

    public enum Status { Missing, Damaged, Loaded }

    /// What is actually on disk, told apart properly. "Could not read" must never look like "empty":
    /// a caller about to write would otherwise replace a damaged library with nothing.
    public readonly record struct State(Status Status, LibraryManifest? Manifest);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SecondPrecisionDateConverter() },
    };

    public static string PathIn(string root) => Path.Combine(root, "library.json");

    public static State Read(string root)
    {
        var path = PathIn(root);
        if (!System.IO.File.Exists(path)) return new(Status.Missing, null);
        try
        {
            var manifest = JsonSerializer.Deserialize<LibraryManifest>(System.IO.File.ReadAllText(path), Json);
            return manifest is null ? new(Status.Damaged, null) : new(Status.Loaded, manifest);
        }
        catch { return new(Status.Damaged, null); }
    }

    /// For readers that only want the entries: a missing or damaged manifest gives an empty list.
    public static LibraryManifest Load(string root) => Read(root).Manifest ?? new();

    /// Atomic: write a sibling then replace, so a reader never sees half a file.
    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = PathIn(root);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public WallpaperEntry? Find(string id) => Wallpapers.FirstOrDefault(w => w.Id == id);

    /// Where a manifest path really points. An absolute path is returned as is; a relative one is
    /// under the root. Written once because getting it wrong is silent: appending a rooted path to
    /// the root would name a file nobody ever wrote.
    public static string Resolve(string root, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
}

/// Swift writes ISO-8601 to the second (2026-10-23T03:06:47Z); keep the files identical in shape.
internal sealed class SecondPrecisionDateConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd\\THH:mm:ss\\Z"));
}
