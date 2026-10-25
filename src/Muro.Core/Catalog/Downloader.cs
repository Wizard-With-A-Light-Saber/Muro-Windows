using Muro.Core.Library;

namespace Muro.Core.Catalog;

/// Downloads a catalog wallpaper into the library, on demand. The video goes to a hidden partial
/// name and is renamed once its size checks out, so a cut-short download never sits under a real
/// name; cancelling or failing removes the partial.
public static class Downloader
{
    public static async Task<WallpaperEntry> DownloadAsync(
        HttpClient http, CatalogEntry remote, string root, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (DownloadFolder.GetLocation(root) is DownloadFolder.Location.Unavailable unavailable)
            throw new DownloadFolder.MoveException(DownloadFolder.MoveError.NotConnected,
                "Connect " + DownloadFolder.PlaceName(unavailable.Path) + " first. Your wallpapers are on it.");

        var extension = Path.GetExtension(new Uri(remote.Video).AbsolutePath);
        if (string.IsNullOrEmpty(extension)) extension = ".mov";
        var masterRelative = "Masters/" + remote.Id + extension;
        var thumbRelative = "Thumbnails/" + remote.Id + ".jpg";
        var master = LibraryManifest.Resolve(root, masterRelative);
        var thumb = LibraryManifest.Resolve(root, thumbRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(master)!);
        Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);

        var partial = Path.Combine(Path.GetDirectoryName(master)!, "." + Path.GetFileName(master) + ".partial");
        try
        {
            var size = await CopyToAsync(http, remote.Video, partial, remote.SizeBytes, progress, ct).ConfigureAwait(false);
            if (remote.SizeBytes > 0 && size != remote.SizeBytes)
                throw new IOException("The download was incomplete (" + size + " of " + remote.SizeBytes + " bytes).");
            File.Move(partial, master, overwrite: true);

            // A missing thumbnail costs the card its picture and nothing more.
            try { await CopyToAsync(http, remote.Thumbnail, thumb, 0, null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { thumbRelative = ""; }

            var entry = new WallpaperEntry
            {
                Id = remote.Id,
                Title = remote.Title,
                Category = remote.Category,
                File = masterRelative,
                Thumbnail = thumbRelative,
                Width = remote.Width,
                Height = remote.Height,
                Fps = remote.Fps,
                Duration = remote.Duration,
                SizeBytes = size,
                DateAdded = DateTime.UtcNow,
            };
            LibraryWriter.Update(root, m =>
            {
                m.Wallpapers.RemoveAll(w => w.Id == entry.Id);
                m.Wallpapers.Add(entry);
            });
            progress?.Report(1);
            return entry;
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    /// Streams a URL to a file. Reports progress in steps of about one percent.
    private static async Task<long> CopyToAsync(
        HttpClient http, string url, string path, long expected, IProgress<double>? progress, CancellationToken ct)
    {
        var temp = path + ".tmp";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expected;

            long written = 0;
            double reported = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 18];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (progress is not null && total > 0)
                    {
                        var done = Math.Min(1.0, (double)written / total);
                        if (done - reported >= 0.01) { reported = done; progress.Report(done); }
                    }
                }
            }
            File.Move(temp, path, overwrite: true);
            return written;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
