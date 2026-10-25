using Muro.Core;
using Muro.Core.Catalog;
using Muro.Core.Library;
using Muro.Core.Media;
using Muro.Platform;

// muro: command line for the Muro library. The same code the app uses, so anything done here shows up
// in the app and the engine (set MURO_HOME to use a throwaway library).
const string Usage = """
    muro catalog [--category X] [--search TEXT] [--new]   list the online catalog
    muro download <id|title>...                            download wallpapers
    muro import <video>...                                 import your own videos
    muro library                                           list your wallpapers
    muro delete <id|title>... [--yes]                      delete wallpapers and every reference
    muro set <id|title> [--monitor N] [--efficient]        play a wallpaper (all monitors, or one)
    muro clear | pause | resume                            stop, pause or resume playback
    muro usage                                             disk use per wallpaper
    muro folder [<path> | --default]                       show or move the download folder
    muro sweep                                             remove files no wallpaper uses
    muro encoders                                          show which video encoders work
    """;

if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }

var root = AppPaths.Root;
AppPaths.Ensure();
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Muro-Windows");
var rest = args.Skip(1).ToList();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return args[0] switch
    {
        "catalog" => await CatalogCommand(),
        "download" => await DownloadCommand(),
        "import" => await ImportCommand(),
        "library" => LibraryCommand(),
        "delete" => DeleteCommand(),
        "set" => await SetCommand(),
        "clear" => Mutate(c => { c.AllDisplays = null; c.PerDisplay.Clear(); }, "Cleared."),
        "pause" => Mutate(c => c.Paused = true, "Paused."),
        "resume" => Mutate(c => c.Paused = null, "Resumed."),
        "usage" => UsageCommand(),
        "folder" => await FolderCommand(),
        "sweep" => Sweep(),
        "encoders" => await EncodersCommand(),
        _ => Fail("Unknown command: " + args[0] + "\n\n" + Usage),
    };
}
catch (OperationCanceledException) { Console.WriteLine("\nCancelled."); return 130; }
catch (CatalogException ex) { return Fail(ex.Title + ". " + ex.Detail); }
catch (Exception ex) when (ex is IOException or TranscodeException or HttpRequestException) { return Fail(ex.Message); }

static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

string? Opt(string name) => rest.Contains(name) && rest.IndexOf(name) + 1 < rest.Count ? rest[rest.IndexOf(name) + 1] : null;
bool Flag(string name) => rest.Contains(name);

// Words that are not options or option values: the ids, titles and paths a command acts on.
List<string> Targets()
{
    var valued = new[] { "--category", "--search", "--monitor" };
    return rest.Where((a, i) => !a.StartsWith("--") && (i == 0 || !valued.Contains(rest[i - 1]))).ToList();
}

string Short(string id) => id.Length > 8 ? id[..8] : id;
string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";

// An id, an id prefix (6+ characters) or a unique case-insensitive part of a title.
T? Pick<T>(string selector, IEnumerable<T> items, Func<T, string> id, Func<T, string> title) where T : class
{
    var all = items.ToList();
    var exact = all.FirstOrDefault(i => id(i) == selector);
    if (exact is not null) return exact;
    var matches = all.Where(i => (selector.Length >= 6 && id(i).StartsWith(selector, StringComparison.OrdinalIgnoreCase))
                                 || title(i).Contains(selector, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 1) return matches[0];
    Console.Error.WriteLine(matches.Count == 0
        ? "Nothing matches \"" + selector + "\"."
        : "\"" + selector + "\" matches " + matches.Count + " wallpapers; use more of the title or the id:\n  " + string.Join("\n  ", matches.Take(8).Select(m => Short(id(m)) + "  " + title(m))));
    return null;
}

async Task<RemoteCatalog> FetchCatalog() =>
    await RemoteCatalog.FetchAsync(http, Environment.GetEnvironmentVariable("MURO_CATALOG") ?? RemoteCatalog.DefaultUrl, cts.Token);

async Task<int> CatalogCommand()
{
    var catalog = await FetchCatalog();
    var seenPath = AppPaths.File("seen-catalog.json");
    HashSet<string>? seen = null;
    try { seen = System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(seenPath)); } catch { }
    var (newIds, nowSeen) = CatalogArrivals.Note(catalog.Wallpapers, seen, DateTime.UtcNow);
    File.WriteAllText(seenPath, System.Text.Json.JsonSerializer.Serialize(nowSeen));

    var owned = LibraryManifest.Load(root).Wallpapers.Select(w => w.Id).ToHashSet();
    var list = catalog.Wallpapers.AsEnumerable();
    if (Opt("--category") is { } category) list = list.Where(w => w.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    if (Opt("--search") is { } text) list = list.Where(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
    if (Flag("--new")) list = list.Where(w => newIds.Contains(w.Id));
    var shown = list.OrderBy(w => w.Category).ThenBy(w => w.Title).ToList();
    foreach (var w in shown)
        Console.WriteLine(Short(w.Id) + "  " + (newIds.Contains(w.Id) ? "NEW " : "    ") + (owned.Contains(w.Id) ? "[have] " : "       ")
                          + w.Category.PadRight(11) + w.Title.PadRight(44) + w.Width + "x" + w.Height + "@" + (int)Math.Round(w.Fps) + "  " + Megabytes(w.SizeBytes));
    Console.WriteLine(shown.Count + " of " + catalog.Wallpapers.Count + " wallpapers.");
    return 0;
}

async Task<int> DownloadCommand()
{
    if (Targets().Count == 0) return Fail("Name a wallpaper to download.");
    var catalog = await FetchCatalog();
    foreach (var selector in Targets())
    {
        var item = Pick(selector, catalog.Wallpapers, w => w.Id, w => w.Title);
        if (item is null) return 1;
        if (LibraryManifest.Load(root).Find(item.Id) is not null) { Console.WriteLine(item.Title + " is already in your library."); continue; }
        Console.Write("Downloading " + item.Title + " (" + Megabytes(item.SizeBytes) + ")  ");
        var progress = new Progress<double>(p => Console.Write("\r" + "Downloading " + item.Title + "  " + (int)(p * 100) + "%   "));
        await Downloader.DownloadAsync(http, item, root, progress, cts.Token);
        Console.WriteLine("\rDownloaded " + item.Title + ".                          ");
    }
    return 0;
}

async Task<int> ImportCommand()
{
    var ffmpeg = Ffmpeg.Locate();
    if (ffmpeg is null) return Fail("ffmpeg was not found. Run scripts/fetch-native.ps1 or set MURO_FFMPEG.");
    var files = Targets();
    if (files.Count == 0) return Fail("Name a video to import.");
    var failed = 0;
    foreach (var file in files)
    {
        if (!File.Exists(file) || !Importer.IsVideo(file)) { Console.Error.WriteLine(file + ": not a video file Muro imports."); failed++; continue; }
        try
        {
            var progress = new Progress<double>(p => Console.Write("\rImporting " + Path.GetFileName(file) + "  " + (int)(p * 100) + "%   "));
            var result = await Importer.ImportAsync(ffmpeg, root, file, progress: progress, ct: cts.Token);
            Console.WriteLine(result.Duplicate
                ? "\r" + result.Entry.Title + " is already in your library.          "
                : "\rImported " + result.Entry.Title + " (" + result.Entry.Width + "x" + result.Entry.Height + "@" + (int)Math.Round(result.Entry.Fps) + ", " + Megabytes(result.Entry.SizeBytes) + ").       ");
        }
        catch (Exception ex) when (ex is TranscodeException or IOException) { Console.Error.WriteLine("\r" + file + ": " + ex.Message); failed++; }
    }
    return failed == 0 ? 0 : 1;
}

int LibraryCommand()
{
    var manifest = LibraryManifest.Read(root);
    if (manifest.Status == LibraryManifest.Status.Damaged) return Fail(new ManifestUnreadableException().Message);
    var entries = manifest.Manifest?.Wallpapers ?? [];
    foreach (var w in entries.OrderBy(w => w.Category).ThenBy(w => w.Title))
        Console.WriteLine(Short(w.Id) + "  " + w.Category.PadRight(11) + w.Title.PadRight(44) + w.Width + "x" + w.Height + "@" + (int)Math.Round(w.Fps)
                          + (w.EfficientFile is null ? "" : "  +30fps") + "  " + Megabytes(LibraryUsage.BytesOf(root, w)));
    Console.WriteLine(entries.Count + " wallpapers.");
    return 0;
}

int DeleteCommand()
{
    var library = LibraryManifest.Load(root).Wallpapers;
    var doomed = new List<WallpaperEntry>();
    foreach (var selector in Targets())
    {
        var item = Pick(selector, library, w => w.Id, w => w.Title);
        if (item is null) return 1;
        doomed.Add(item);
    }
    if (doomed.Count == 0) return Fail("Name a wallpaper to delete.");
    if (!Flag("--yes"))
    {
        Console.Write("Delete " + string.Join(", ", doomed.Select(d => d.Title)) + " (" + Megabytes(doomed.Sum(d => LibraryUsage.BytesOf(root, d))) + ")? This cannot be undone. [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Nothing deleted."); return 0; }
    }
    var result = WallpaperDeletion.Delete(root, doomed.Select(d => d.Id).ToHashSet());
    Console.WriteLine("Deleted " + doomed.Count + (result.ClearedDesktop ? " (and took it off the desktop)." : "."));
    return 0;
}

async Task<int> SetCommand()
{
    var target = Targets().FirstOrDefault();
    if (target is null) return Fail("Name a wallpaper to play.");
    var entry = Pick(target, LibraryManifest.Load(root).Wallpapers, w => w.Id, w => w.Title);
    if (entry is null) return 1;

    var mode = Flag("--efficient") ? "efficient" : "smooth";
    if (mode == "efficient" && entry.Fps > EfficientVariant.MinimumFps && entry.EfficientFile is null)
    {
        var ffmpeg = Ffmpeg.Locate();
        if (ffmpeg is not null)
        {
            var progress = new Progress<double>(p => Console.Write("\rMaking the 30 fps version  " + (int)(p * 100) + "%   "));
            await EfficientVariant.EnsureAsync(ffmpeg, root, entry.Id, progress, cts.Token);
            Console.WriteLine();
        }
    }

    var config = EngineConfig.Load(root);
    if (Opt("--monitor") is { } n)
    {
        var monitors = Monitors.All();
        if (!int.TryParse(n, out var index) || index < 1 || index > monitors.Count) return Fail("--monitor must be 1 to " + monitors.Count + " (see Muro.Engine --list-monitors).");
        config.PerDisplay[monitors[index - 1].Id] = new(entry.Id, mode);
    }
    else
    {
        config.AllDisplays = new(entry.Id, mode);
    }
    config.Save(root);
    Console.WriteLine("Playing " + entry.Title + " (" + mode + "). The engine picks it up on its own.");
    return 0;
}

int Mutate(Action<EngineConfig> change, string message)
{
    var config = EngineConfig.Load(root);
    change(config);
    config.Save(root);
    Console.WriteLine(message);
    return 0;
}

int UsageCommand()
{
    var manifest = LibraryManifest.Load(root);
    var sizes = LibraryUsage.PerWallpaper(root, manifest);
    foreach (var w in manifest.Wallpapers.OrderByDescending(w => sizes[w.Id]))
        Console.WriteLine(Megabytes(sizes[w.Id]).PadLeft(10) + "  " + w.Title);
    Console.WriteLine(Megabytes(sizes.Values.Sum()).PadLeft(10) + "  total, in " + manifest.Wallpapers.Count + " wallpapers");
    return 0;
}

async Task<int> FolderCommand()
{
    if (Flag("--default") || Targets().Count > 0)
    {
        var picked = Flag("--default") ? null : Targets()[0];
        try
        {
            await DownloadFolderMove.MoveAsync(root, picked,
                progress: (done, total) => Console.Write("\rMoving " + done + " of " + total + " videos  "),
                ct: cts.Token);
        }
        catch (DownloadFolder.MoveException ex) { Console.WriteLine(); return Fail(ex.Message); }
        Console.WriteLine();
    }
    Console.WriteLine(DownloadFolder.GetLocation(root) switch
    {
        DownloadFolder.Location.Custom c => "Wallpapers are kept in " + c.Path,
        DownloadFolder.Location.Unavailable u => "Wallpapers are kept in " + u.Path + ", which is not connected.",
        _ => "Wallpapers are kept in the Muro library folder: " + DownloadFolder.MastersPath(root),
    });
    return 0;
}

int Sweep()
{
    Console.WriteLine("Freed " + Megabytes(LibraryWriter.SweepOrphans(root)) + ".");
    return 0;
}

async Task<int> EncodersCommand()
{
    var ffmpeg = Ffmpeg.Locate();
    if (ffmpeg is null) return Fail("ffmpeg was not found.");
    Console.WriteLine("ffmpeg: " + ffmpeg);
    var working = await Encoders.DetectAsync(ffmpeg, cts.Token);
    foreach (var e in working) Console.WriteLine("  works: " + e.Name.PadRight(12) + e.Description + (e == working[0] ? "   <- used first" : ""));
    if (working.Count == 0) Console.WriteLine("  no video encoder works.");
    return 0;
}
