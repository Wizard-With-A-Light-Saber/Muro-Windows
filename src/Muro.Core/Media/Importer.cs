using Muro.Core.Library;

namespace Muro.Core.Media;

public sealed record ImportResult(WallpaperEntry Entry, bool Duplicate);

/// Brings a user video into the library: transcode once, thumbnail, manifest row. Dropping the same
/// file twice returns the existing wallpaper instead of making a second identical one.
public static class Importer
{
    public static readonly string[] Extensions = [".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi"];

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// The entry a source was already imported as, or null. A fingerprint match wins; an older import
    /// without one is matched by the same name and nearly the same length.
    public static WallpaperEntry? AlreadyImported(string source, string? fingerprint, IEnumerable<WallpaperEntry> entries, double duration)
    {
        var list = entries.ToList();
        if (fingerprint is not null && list.FirstOrDefault(e => e.SourceFingerprint == fingerprint) is { } match) return match;
        var title = Path.GetFileNameWithoutExtension(source);
        return duration > 0
            ? list.FirstOrDefault(e => e.SourceFingerprint is null && e.IsImported && e.Title == title && Math.Abs(e.Duration - duration) < 0.1)
            : null;
    }

    public static async Task<ImportResult> ImportAsync(
        string ffmpeg, string root, string source, string? title = null, string? category = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = await Ffmpeg.ProbeAsync(ffmpeg, source, ct).ConfigureAwait(false)
                   ?? throw new TranscodeException(Path.GetFileName(source) + " is not a video Muro can read.");

        var fingerprint = Fingerprint.Of(source);
        if (AlreadyImported(source, fingerprint, LibraryManifest.Load(root).Wallpapers, info.Duration) is { } existing)
            return new ImportResult(existing, true);

        if (DownloadFolder.GetLocation(root) is DownloadFolder.Location.Unavailable gone)
            throw new DownloadFolder.MoveException(DownloadFolder.MoveError.NotConnected,
                "Connect " + DownloadFolder.PlaceName(gone.Path) + " first. Your wallpapers are on it.");

        var id = Guid.NewGuid().ToString();
        var masterRelative = "Masters/" + id + ".mov";
        var thumbRelative = "Thumbnails/" + id + ".jpg";
        var master = LibraryManifest.Resolve(root, masterRelative);
        var thumb = LibraryManifest.Resolve(root, thumbRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(master)!);
        Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
        var partial = Path.Combine(Path.GetDirectoryName(master)!, "." + id + ".mov.partial");

        try
        {
            // The encode is nearly all of an import, so it is most of the bar; the picture is the rest.
            var scaled = progress is null ? null : new Progress<double>(p => progress.Report(p * 0.95));
            var keepAudio = EngineConfig.Load(root).LiveSound;
            var output = await Transcoder.TranscodeAsync(ffmpeg, source, partial, info, halveFps: false, scaled, ct, keepAudio: keepAudio).ConfigureAwait(false);
            File.Move(partial, master, overwrite: true);

            // A missing thumbnail costs the card its picture and must not fail a good import.
            if (!await Transcoder.ThumbnailAsync(ffmpeg, master, thumb, output.Duration, ct).ConfigureAwait(false))
                thumbRelative = "";

            var entry = new WallpaperEntry
            {
                Id = id,
                Title = title ?? Path.GetFileNameWithoutExtension(source),
                Category = category ?? WallpaperEntry.ImportedCategory,
                File = masterRelative,
                Thumbnail = thumbRelative,
                Width = output.Width,
                Height = output.Height,
                Fps = output.Fps,
                Duration = output.Duration,
                SizeBytes = new FileInfo(master).Length,
                DateAdded = DateTime.UtcNow,
                SourceFingerprint = fingerprint,
                HasSound = keepAudio,
            };
            LibraryWriter.Update(root, m => m.Wallpapers.Add(entry));
            progress?.Report(1);
            return new ImportResult(entry, false);
        }
        catch
        {
            foreach (var path in new[] { partial, master, thumb }) { try { File.Delete(path); } catch { } }
            throw;
        }
    }
}

/// The Efficient playback mode: a lighter copy of a wallpaper. A high frame rate one gets a real 30 fps file
/// (presenting every second frame saves little, because the decoder still decodes all of them); one much bigger
/// than the biggest screen is shrunk to the screen, since the GPU would decode pixels that are never shown.
/// Generated lazily the first time a wallpaper is set to Efficient.
public static class EfficientVariant
{
    /// Sources at or below this are already cheap enough.
    public const double MinimumFps = 40;

    /// The biggest monitor in physical pixels. Muro.Platform's Monitors.All() keeps it current; 0 means unknown,
    /// and then a wallpaper is never shrunk.
    public static (int Width, int Height) Screen { get; set; }

    /// The size of the copy, or null when the wallpaper is not much bigger than the screen. It covers the screen
    /// (the player crops to fill), has even sides, and is only made when the source is over 1.25x the screen
    /// on a side, so a 1440p wallpaper on a 1080p screen shrinks and a 1200p one does not.
    public static (int Width, int Height)? TargetSize(int width, int height, (int Width, int Height) screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0 || width <= 0 || height <= 0) return null;
        var scale = Math.Max(screen.Width / (double)width, screen.Height / (double)height);
        if (scale > 0.8) return null;
        return ((int)Math.Round(width * scale / 2) * 2, (int)Math.Round(height * scale / 2) * 2);
    }

    public static (int Width, int Height)? TargetSize(int width, int height) => TargetSize(width, height, Screen);

    /// Whether this wallpaper's Efficient copy is still to be made: it has none yet and it is worth one.
    public static bool Needs(WallpaperEntry entry) =>
        entry.ActiveLook != entry.BakedLook
        || (entry.EfficientFile is null && (entry.Fps > MinimumFps || TargetSize(entry.Width, entry.Height) is not null));

    /// Whether a copy is worth having at all: a look to bake, a high frame rate, or a source much bigger than the screen.
    private static bool Worthwhile(WallpaperEntry entry) =>
        entry.ActiveLook is not null || entry.Fps > MinimumFps || TargetSize(entry.Width, entry.Height) is not null;

    public static async Task<bool> EnsureAsync(
        string ffmpeg, string root, string id, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var entry = LibraryManifest.Load(root).Find(id);
        if (entry is null) return false;
        if (!Needs(entry)) return true;

        var relative = "Masters/" + id + "-eff.mov";
        // The look was taken off a wallpaper that has no other reason for a copy: remove the looked copy it left.
        if (!Worthwhile(entry))
        {
            try { File.Delete(LibraryManifest.Resolve(root, relative)); } catch { }
            LibraryWriter.Update(root, m => { if (m.Find(id) is { } current) { current.EfficientFile = null; current.BakedLook = null; } });
            return true;
        }
        var look = entry.ActiveLook;
        var source = LibraryManifest.Resolve(root, entry.File);
        var destination = LibraryManifest.Resolve(root, relative);
        var partial = Path.Combine(Path.GetDirectoryName(destination)!, "." + id + "-eff.mov.partial");
        var info = await Ffmpeg.ProbeAsync(ffmpeg, source, ct).ConfigureAwait(false);
        if (info is null) return false;

        try
        {
            await Transcoder.TranscodeAsync(ffmpeg, source, partial, info, halveFps: entry.Fps > MinimumFps, progress, ct, TargetSize(info.Width, info.Height), Looks.Filter(look)).ConfigureAwait(false);
            File.Move(partial, destination, overwrite: true);
            var stillThere = false;
            LibraryWriter.Update(root, m =>
            {
                if (m.Find(id) is not { } current) return;
                current.EfficientFile = relative;
                current.BakedLook = look;
                stillThere = true;
            });
            // Deleted while it was encoding: do not leave the variant behind.
            if (!stillThere) File.Delete(destination);
            return stillThere;
        }
        catch
        {
            foreach (var path in new[] { partial }) { try { File.Delete(path); } catch { } }
            throw;
        }
    }
}
