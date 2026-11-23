using Muro.Core.Catalog;

namespace Muro.Core.Library;

/// Brings your own interactive wallpaper into the library: an HTML page, or a folder with one. It is copied into
/// <c>Hosted Interactive/&lt;id&gt;/</c> beside the downloaded ones, so playing, deleting and the engine's locked-down page
/// host treat it the same; it is filed under "My Videos", which keeps it out of Explore. A page has no picture of its own,
/// so the caller supplies a still and a thumbnail. The copy is bounded (a page cannot be a way to fill the disk), and
/// the page can reach only its own folder and never the network, whatever it asks for (see the engine's InteractiveLayer).
public static class InteractiveImport
{
    private static readonly string[] PageExtensions = [".html", ".htm"];

    public static bool IsPageFile(string path) => File.Exists(path) && PageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsPageFolder(string path) => Directory.Exists(path) && FindPage(path) is not null;

    public static bool IsPage(string path) => IsPageFile(path) || IsPageFolder(path);

    /// The page a folder opens with: index.html (or .htm), or the only page in it. Null when there is none, or several
    /// and none is called index.
    public static string? FindPage(string folder)
    {
        foreach (var name in new[] { "index.html", "index.htm" })
            if (File.Exists(Path.Combine(folder, name))) return name;
        var pages = Directory.EnumerateFiles(folder).Where(IsPageFile).Select(Path.GetFileName).ToList();
        return pages.Count == 1 ? pages[0] : null;
    }

    /// The page's own title, else the file or folder name.
    public static string TitleOf(string source)
    {
        var fallback = Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/'));
        try
        {
            var page = Directory.Exists(source) ? Path.Combine(source, FindPage(source)!) : source;
            var head = File.ReadAllText(page);
            head = head[..Math.Min(head.Length, 8192)];
            var open = head.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
            var close = open < 0 ? -1 : head.IndexOf("</title>", open, StringComparison.OrdinalIgnoreCase);
            if (open >= 0 && close > open && head[(open + 7)..close].Trim() is { Length: > 0 } title)
                return title.Length > 80 ? title[..80] : title;
        }
        catch (IOException) { }
        return string.IsNullOrWhiteSpace(fallback) ? "My interactive wallpaper" : fallback;
    }

    /// Copies the page into the library. Throws <see cref="IOException"/> with a sentence for the person when the source
    /// is too big or unreadable; nothing is left behind then.
    public static WallpaperEntry Import(string root, string source, byte[] still, byte[] thumb)
    {
        var id = "mine-" + Guid.NewGuid().ToString("N")[..12];
        var home = HostedInteractive.FolderFor(root, id);
        var staging = Path.Combine(root, HostedInteractive.FolderName, "." + id + ".partial");
        Directory.CreateDirectory(Path.GetDirectoryName(home)!);
        Directory.CreateDirectory(staging);
        try
        {
            string page;
            long total = 0;
            if (Directory.Exists(source))
            {
                page = FindPage(source) ?? throw new IOException("There is no page to open in that folder. Give it an index.html.");
                var count = 0;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(source, file);
                    var info = new FileInfo(file);
                    // Hidden files and links are not part of a wallpaper; a link could lead out of the folder.
                    if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        || relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'))) continue;
                    if (++count > HostedInteractive.MostFiles) throw new IOException("That folder has too many files for a wallpaper (the limit is " + HostedInteractive.MostFiles + ").");
                    if (info.Length > HostedInteractive.LargestFile) throw new IOException(relative + " is too big (the limit is 100 MB for one file).");
                    total += info.Length;
                    if (total > HostedInteractive.LargestWallpaper) throw new IOException("That folder is too big for a wallpaper (the limit is 300 MB).");
                    var target = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
            else
            {
                if (!IsPageFile(source)) throw new IOException("That is not an HTML page.");
                var info = new FileInfo(source);
                if (info.Length > HostedInteractive.LargestFile) throw new IOException("That page is too big (the limit is 100 MB).");
                page = "index.html";
                File.Copy(source, Path.Combine(staging, page));
                total = info.Length;
            }

            // The picture the card and the lock screen use. Written after the copy so a page's own still.jpg never wins.
            File.WriteAllBytes(Path.Combine(staging, "still.jpg"), still);
            File.WriteAllBytes(Path.Combine(staging, "thumb.jpg"), thumb);
            Directory.Move(staging, home);

            var relativeHome = HostedInteractive.FolderName + "/" + id + "/";
            var entry = new WallpaperEntry
            {
                Id = id,
                Title = TitleOf(source),
                Category = WallpaperEntry.ImportedCategory,
                File = relativeHome + "still.jpg",
                Thumbnail = relativeHome + "thumb.jpg",
                InteractivePage = relativeHome + page.Replace('\\', '/'),
                SizeBytes = total + still.Length + thumb.Length,
                DateAdded = DateTime.UtcNow,
            };
            LibraryWriter.Update(root, m => m.Wallpapers.Add(entry));
            return entry;
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }
}
