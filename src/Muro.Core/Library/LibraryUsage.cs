namespace Muro.Core.Library;

/// What each wallpaper costs on disk. Sizes are read from the files, not the manifest, so a
/// variant or preview generated later is counted and a file that vanished counts as zero.
public static class LibraryUsage
{
    public static long BytesOf(string root, WallpaperEntry entry)
    {
        long total = 0;
        foreach (var relative in entry.RelativeFiles)
        {
            try { total += new FileInfo(LibraryManifest.Resolve(root, relative)).Length; } catch { }
        }
        return total;
    }

    public static Dictionary<string, long> PerWallpaper(string root, LibraryManifest manifest) =>
        manifest.Wallpapers.ToDictionary(w => w.Id, w => BytesOf(root, w));
}
