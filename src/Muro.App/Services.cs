using System.Diagnostics;
using Muro.Core;
using Muro.Core.Catalog;
using Muro.Core.Library;
using Muro.Core.Media;
using Muro.Core.Schedule;
using Muro.Core.Updates;
using Muro.Platform;

namespace Muro.App;

/// Process-wide services. The pages hold no state of their own beyond what they show.
internal static class Services
{
    public static string Root => AppPaths.Root;
    public static HttpClient Http { get; } = CreateHttp();
    public static CatalogService Catalog { get; } = new();
    public static LibraryService Library { get; } = new();
    public static DownloadService Downloads { get; } = new();
    public static UpdateService Updates { get; } = new();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Muro-Windows");
        return http;
    }
}

internal sealed class LibraryService
{
    public LibraryManifest Manifest { get; private set; } = new();
    public HashSet<string> Ids { get; private set; } = [];
    /// library.json exists and cannot be read. Nothing may write to it.
    public bool Damaged { get; private set; }
    public event Action? Changed;

    public LibraryService() => Reload(raise: false);

    public void Reload(bool raise = true)
    {
        var state = LibraryManifest.Read(Services.Root);
        Damaged = state.Status == LibraryManifest.Status.Damaged;
        Manifest = state.Manifest ?? new LibraryManifest();
        Ids = Manifest.Wallpapers.Select(w => w.Id).ToHashSet();
        if (raise) Changed?.Invoke();
    }
}

internal sealed class CatalogService
{
    private DateTime _last = DateTime.MinValue;
    private Task? _running;
    private HashSet<string>? _seen;
    private bool _seenLoaded;

    public IReadOnlyList<CatalogEntry> Items { get; private set; } = [];
    public HashSet<string> NewIds { get; } = [];
    public CatalogException? Error { get; private set; }
    public bool Loaded { get; private set; }
    public event Action? Changed;

    public static string Url => Environment.GetEnvironmentVariable("MURO_CATALOG") ?? RemoteCatalog.DefaultUrl;

    /// Fetched on launch and whenever the window comes to the front, so a newly published wallpaper
    /// shows up without an app update. Throttled so rapid focus changes do not hammer the CDN.
    public async Task RefreshAsync(bool force = false)
    {
        if (_running is { IsCompleted: false }) { await _running; return; }
        if (!force && DateTime.UtcNow - _last < TimeSpan.FromSeconds(30)) return;
        _running = FetchAsync();
        await _running;
    }

    private async Task FetchAsync()
    {
        try
        {
            var catalog = await RemoteCatalog.FetchAsync(Services.Http, Url);
            Items = catalog.Wallpapers;
            Error = null;
            NoteArrivals();
        }
        catch (CatalogException ex)
        {
            // The last good catalog stays: one failed refresh must not empty an Explore that worked a moment ago.
            Error = ex;
        }
        finally
        {
            Loaded = true;
            _last = DateTime.UtcNow;
            Changed?.Invoke();
        }
    }

    private void NoteArrivals()
    {
        var path = AppPaths.File("seen-catalog.json");
        if (!_seenLoaded)
        {
            _seenLoaded = true;
            try { _seen = System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)); } catch { }
        }
        var (news, seen) = CatalogArrivals.Note(Items, _seen, DateTime.UtcNow);
        // A badge lasts the session: it marks what arrived since you last looked.
        NewIds.UnionWith(news);
        _seen = seen;
        try { File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(seen)); } catch { }
    }
}

/// Looks for a newer Windows build on GitHub. The automatic check is silent on every failure (offline,
/// rate-limited, no release yet); only a check the user asked for reports "up to date" or "failed",
/// because someone who pressed a button deserves an answer.
internal sealed class UpdateService
{
    public enum State { Idle, Checking, UpToDate, Available, Failed }

    /// Long enough to stay far inside GitHub's unauthenticated rate limit, short enough to hear about a release the day it lands.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private DateTime _last = DateTime.MinValue;

    public State Current { get; private set; }
    /// The newer release, while one is on offer.
    public ReleaseInfo? Latest { get; private set; }
    public event Action? Changed;

    /// MURO_RELEASES_URL points the check at another feed, for trying the update flow without publishing.
    private static string Url => Environment.GetEnvironmentVariable("MURO_RELEASES_URL") ?? UpdateChecker.ReleasesUrl;

    public Task CheckIfDueAsync() => DateTime.UtcNow - _last < Interval ? Task.CompletedTask : CheckAsync(userInitiated: false);

    public async Task CheckAsync(bool userInitiated)
    {
        if (Current == State.Checking) return;
        _last = DateTime.UtcNow;
        if (userInitiated) Set(State.Checking);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Latest = await UpdateChecker.FindAsync(Services.Http, AppVersion.Current, Url, timeout.Token);
            Set(Latest is not null ? State.Available : userInitiated ? State.UpToDate : State.Idle);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // A failed automatic check keeps whatever was known; a failed manual one says so.
            if (userInitiated) Set(State.Failed);
            else if (Current == State.Checking) Set(Latest is null ? State.Idle : State.Available);
        }
    }

    private void Set(State state)
    {
        Current = state;
        Changed?.Invoke();
    }

    /// Download the update: the installer when the release has one, its page otherwise. Never a dead end.
    public void OpenDownload()
    {
        if (Latest is null) return;
        Process.Start(new ProcessStartInfo((Latest.DownloadUrl ?? Latest.Page).AbsoluteUri) { UseShellExecute = true });
        MarkSeen();
    }

    /// Opening the update, or dismissing its banner, is how someone acknowledges it: the banner is shown once per version.
    public void MarkSeen()
    {
        // Best effort: if the file is busy the banner just comes back once more.
        try { if (Latest is { } release) AppSettings.Update(Services.Root, s => s.SeenUpdateVersion = release.Version); }
        catch (IOException) { }
        Changed?.Invoke();
    }

    public bool BannerVisible => Current == State.Available && Latest is { } r && AppSettings.Load(Services.Root).SeenUpdateVersion != r.Version;
}

/// Downloads started from Explore. Must be started on the UI thread so progress arrives there.
internal sealed class DownloadService
{
    private sealed class State
    {
        public double Progress;
        public readonly CancellationTokenSource Cts = new();
    }

    private readonly Dictionary<string, State> _active = [];

    /// Raised with the wallpaper id whenever its state or progress changes.
    public event Action<string>? Changed;
    public event Action<CatalogEntry, string>? Failed;

    public bool IsActive(string id) => _active.ContainsKey(id);
    public double ProgressOf(string id) => _active.TryGetValue(id, out var s) ? s.Progress : 0;

    public async Task StartAsync(CatalogEntry entry)
    {
        if (_active.ContainsKey(entry.Id) || Services.Library.Damaged) return;
        var state = new State();
        _active[entry.Id] = state;
        Changed?.Invoke(entry.Id);
        try
        {
            var progress = new Progress<double>(p => { state.Progress = p; Changed?.Invoke(entry.Id); });
            await Downloader.DownloadAsync(Services.Http, entry, Services.Root, progress, state.Cts.Token);
            Services.Library.Reload();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Failed?.Invoke(entry, ex.Message); }
        finally
        {
            _active.Remove(entry.Id);
            Changed?.Invoke(entry.Id);
        }
    }

    public void Cancel(string id)
    {
        if (_active.TryGetValue(id, out var state)) state.Cts.Cancel();
    }
}

/// Starts the engine, which owns the wallpaper windows. It enforces one instance per library itself
/// (a mutex), so starting it when it is already running is harmless.
internal static class EngineLauncher
{
    public static void EnsureRunning()
    {
        if (Process.GetProcessesByName("Muro.Engine").Length > 0) return;
        var path = Locate();
        if (path is null) return;
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        });
    }

    public static string? Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "Engine", "Muro.Engine.exe"), Path.Combine(baseDir, "Muro.Engine.exe") })
            if (File.Exists(candidate)) return candidate;
        // Development tree: the engine build output under src/Muro.Engine/bin/Release, next to this app.
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "Muro.Engine", "bin", "Release");
            if (!Directory.Exists(bin)) continue;
            var found = Directory.EnumerateFiles(bin, "Muro.Engine.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }
}

/// Finds Muro.scr, the screen saver program, the way the app finds the engine.
internal static class ScreenSaverFiles
{
    public static string? Locate()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "ScreenSaver", "Muro.scr"), Path.Combine(baseDir, "Muro.scr") })
            if (File.Exists(candidate)) return candidate;
        // Development tree: the screen saver build output under src/Muro.ScreenSaver/bin/Release.
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "Muro.ScreenSaver", "bin", "Release");
            if (!Directory.Exists(bin)) continue;
            var found = Directory.EnumerateFiles(bin, "Muro.scr", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }
}

internal static class PlaybackService
{
    /// Which wallpaper ids are on a screen right now, from the shared config.
    public static HashSet<string> PlayingIds()
    {
        var config = EngineConfig.Load(Services.Root);
        var ids = config.PerDisplay.Values.Select(a => a.WallpaperID).ToHashSet();
        if (config.AllDisplays is { } all) ids.Add(all.WallpaperID);
        return ids;
    }

    /// Plays a library wallpaper on every monitor (monitorIndex null) or one monitor. Efficient mode
    /// first makes the 30 fps version, which can take a little while for a long 4K clip.
    public static async Task ApplyAsync(WallpaperEntry entry, int? monitorIndex, bool efficient, IProgress<double>? progress, CancellationToken ct)
    {
        if (efficient && entry.Fps > EfficientVariant.MinimumFps && entry.EfficientFile is null && Ffmpeg.Locate() is { } ffmpeg)
            await EfficientVariant.EnsureAsync(ffmpeg, Services.Root, entry.Id, progress, ct);

        var config = EngineConfig.Load(Services.Root);
        var mode = efficient ? "efficient" : "smooth";
        if (monitorIndex is { } index)
        {
            var monitors = Monitors.All();
            if (index >= monitors.Count) return;
            config.PerDisplay[monitors[index].Id] = new EngineConfig.Assignment(entry.Id, mode);
        }
        else
        {
            config.AllDisplays = new EngineConfig.Assignment(entry.Id, mode);
            config.PerDisplay.Clear();
        }
        config.Save(Services.Root);
        // A manual choice ends the running schedule, or its next tick would overwrite it.
        ScheduleControl.Stop(Services.Root, Surface.Desktop, restore: false);
        Services.Library.Reload();
        EngineLauncher.EnsureRunning();
    }

    public static void StopAll()
    {
        var config = EngineConfig.Load(Services.Root);
        config.AllDisplays = null;
        config.PerDisplay.Clear();
        config.Save(Services.Root);
    }
}
