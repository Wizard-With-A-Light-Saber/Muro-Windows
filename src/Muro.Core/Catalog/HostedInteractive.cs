using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Muro.Core.Library;

namespace Muro.Core.Catalog;

/// Interactive wallpapers hosted beside catalog.json (the macOS app has them since 6.1; since 6.2 from the server):
///   interactive.json                       the list
///   interactive/&lt;id&gt;/&lt;version&gt;/&lt;file&gt;      each wallpaper's files; a changed page gets a new version
/// Like a live wallpaper, one is downloaded only when the person asks. It lands in
/// <c>Hosted Interactive/&lt;id&gt;/</c> (index.html, still.jpg, thumb.jpg), and every file is checked against the size
/// and SHA-256 in the list before the folder is put in place, so nothing swapped on the server can run here.
/// The library entry plays <c>still.jpg</c> as the wallpaper (lock screen, screen saver and anything that is not the
/// desktop) and the engine draws the page over it on the desktop.
public static class HostedInteractive
{
    public const string ListName = "interactive.json";
    public const string FolderName = "Hosted Interactive";
    public const string Category = "Interactive Wallpapers";
    public static readonly string[] RequiredFiles = ["index.html", "thumb.jpg", "still.jpg"];
    public const long LargestFile = 100L * 1024 * 1024;
    public const long LargestWallpaper = 300L * 1024 * 1024;
    /// More files than any real wallpaper has: a list asking for more is wrong.
    public const int MostFiles = 500;

    public sealed class File
    {
        /// Inside the wallpaper's folder, '/' between folders.
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        /// Shown as "By ..." on its page, nowhere else.
        public string? Author { get; set; }
        /// The day it was first published, yyyy-MM-dd.
        public string Added { get; set; } = "";
        public int? Order { get; set; }
        /// Changes whenever any of its files does.
        public string Version { get; set; } = "";
        public List<File> Files { get; set; } = [];

        public long TotalSize => Files.Sum(f => f.Size);
    }

    public static string FolderFor(string root, string id) => System.IO.Path.Combine(root, FolderName, id);

    public static string ListUrl(string catalogUrl) => new Uri(new Uri(catalogUrl), ListName).ToString();

    public static string FileUrl(string listUrl, Entry entry, string path) =>
        new Uri(new Uri(listUrl), "interactive/" + entry.Id + "/" + entry.Version + "/" + path).ToString();

    // ---- What the list may say

    private static bool IsSafeName(string name, bool allowSlash)
    {
        if (name.Length is 0 or > 200) return false;
        foreach (var part in allowSlash ? name.Split('/') : [name])
        {
            if (part.Length == 0 || part[0] == '.' || part.Contains("..")) return false;
            foreach (var c in part)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')) return false;
        }
        return true;
    }

    /// A file name a hosted wallpaper may use: lower case letters, digits, dots, dashes and underscores, in folders,
    /// never leaving its own folder and never hidden.
    public static bool IsSafePath(string path) => IsSafeName(path, allowSlash: true);
    public static bool IsSafeId(string id) => IsSafeName(id, allowSlash: false);
    public static bool IsSafeVersion(string version) => version.Length is > 0 and <= 64 && version.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool IsSha256(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// Whether Muro will download this entry at all.
    public static bool IsUsable(Entry entry)
    {
        if (!IsSafeId(entry.Id) || !IsSafeVersion(entry.Version) || string.IsNullOrWhiteSpace(entry.Title)) return false;
        if (entry.Files.Count is 0 or > MostFiles || entry.TotalSize > LargestWallpaper) return false;
        var seen = new HashSet<string>();
        foreach (var file in entry.Files)
        {
            if (!IsSafePath(file.Path) || !IsSha256(file.Sha256) || file.Size is <= 0 or > LargestFile || !seen.Add(file.Path)) return false;
        }
        return RequiredFiles.All(seen.Contains);
    }

    /// Reads the list. One entry this Muro cannot read is left out, never the whole list, so a newer server can add
    /// things to an entry. Unusable entries are dropped too.
    public static List<Entry> ParseList(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var result = new List<Entry>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("wallpapers", out var items) || items.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in items.EnumerateArray())
        {
            try
            {
                if (item.Deserialize<Entry>(options) is { } entry && IsUsable(entry)) result.Add(entry);
            }
            catch (JsonException) { }
        }
        return result.OrderBy(e => e.Order ?? int.MaxValue).ThenBy(e => e.Added, StringComparer.Ordinal).ToList();
    }

    /// The list, always from the network. A server with no list yet answers an empty one, and a failure here never
    /// breaks the video catalog: Explore just shows no interactive wallpapers.
    public static async Task<List<Entry>> FetchListAsync(HttpClient http, string listUrl, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            return ParseList(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return []; }
    }

    /// What Explore shows for one that is not downloaded, in the shape of a video's catalog entry: its picture is its
    /// thumbnail on the server, its size is all of its files and its date is the day it was published.
    public static CatalogEntry CatalogEntryFor(Entry entry, string listUrl) => new()
    {
        Id = entry.Id,
        Title = entry.Title,
        Category = Category,
        SizeBytes = entry.TotalSize,
        Video = FileUrl(listUrl, entry, "index.html"),
        Thumbnail = FileUrl(listUrl, entry, "thumb.jpg"),
        PublishedAt = DateTime.TryParseExact(entry.Added, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) ? day : null,
        Interactive = entry,
        InteractiveListUrl = listUrl,
    };

    public static string Sha256Of(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---- Downloading

    /// Downloads every file into a hidden folder, checks each one, then swaps the folder in: until the new copy is
    /// complete and checked, an older copy stays where it is. Adds or replaces the library entry.
    public static async Task<WallpaperEntry> DownloadAsync(
        HttpClient http, CatalogEntry remote, string root, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var entry = remote.Interactive ?? throw new ArgumentException("not an interactive wallpaper", nameof(remote));
        var listUrl = remote.InteractiveListUrl ?? ListUrl(RemoteCatalog.DefaultUrl);
        if (!IsUsable(entry)) throw new IOException("This interactive wallpaper is not one Muro can safely download.");
        if (DownloadFolder.GetLocation(root) is DownloadFolder.Location.Unavailable unavailable)
            throw new DownloadFolder.MoveException(DownloadFolder.MoveError.NotConnected,
                "Connect " + DownloadFolder.PlaceName(unavailable.Path) + " first. Your wallpapers are on it.");

        var home = FolderFor(root, entry.Id);
        var staging = System.IO.Path.Combine(root, FolderName, "." + entry.Id + ".partial");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(home)!);
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        Directory.CreateDirectory(staging);
        try
        {
            long done = 0;
            foreach (var file in entry.Files)
            {
                var target = System.IO.Path.Combine(staging, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                using var response = await http.GetAsync(FileUrl(listUrl, entry, file.Path), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var sink = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    // Never trust the server for how much to read: stop one byte past the size the list promised.
                    var buffer = new byte[1 << 16];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        written += read;
                        if (written > file.Size) throw new IOException(file.Path + " is larger than the list says.");
                        await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
                var info = new FileInfo(target);
                if (info.Length != file.Size || Sha256Of(target) != file.Sha256)
                    throw new IOException(file.Path + " did not match what the list promised, so it was not used.");
                done += file.Size;
                progress?.Report(Math.Min(0.99, (double)done / Math.Max(1, entry.TotalSize)));
            }

            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            Directory.Move(staging, home);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }

        var relative = FolderName + "/" + entry.Id + "/";
        var wallpaper = new WallpaperEntry
        {
            Id = entry.Id,
            Title = entry.Title,
            Category = Category,
            File = relative + "still.jpg",
            Thumbnail = relative + "thumb.jpg",
            InteractivePage = relative + "index.html",
            SizeBytes = entry.TotalSize,
            DateAdded = DateTime.UtcNow,
        };
        LibraryWriter.Update(root, m =>
        {
            m.Wallpapers.RemoveAll(w => w.Id == wallpaper.Id);
            m.Wallpapers.Add(wallpaper);
        });
        progress?.Report(1);
        return wallpaper;
    }
}
