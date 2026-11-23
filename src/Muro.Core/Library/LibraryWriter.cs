namespace Muro.Core.Library;

public sealed class ManifestUnreadableException()
    : IOException("Muro could not read your wallpaper list, so it stopped rather than overwrite it. " +
                  "Your wallpaper files are untouched. The list is library.json in the Muro folder under LocalAppData.");

/// Serialises every read-modify-write of library.json. Downloads, imports and the UI all edit the
/// manifest; each used to load, change and save on its own, so two finishing together lost one
/// piece of work. An in-process lock covers threads and an exclusive lock file covers a second
/// process (the CLI running while the app is open).
public static class LibraryWriter
{
    private static readonly object Gate = new();

    /// Reads the manifest, applies the change to it, writes it back. Always work from the manifest
    /// handed to the callback, never from a copy read earlier.
    /// A damaged manifest is never written over; a missing one simply starts a new library.
    public static LibraryManifest Update(string root, Action<LibraryManifest> change)
    {
        lock (Gate)
        {
            using var _ = LockFile(root);
            var state = LibraryManifest.Read(root);
            var manifest = state.Status switch
            {
                LibraryManifest.Status.Loaded => state.Manifest!,
                LibraryManifest.Status.Missing => new LibraryManifest(),
                _ => throw new ManifestUnreadableException(),
            };
            change(manifest);
            manifest.Save(root);
            return manifest;
        }
    }

    /// Removes wallpapers for good: entries leave the manifest first, then their files leave the disk.
    /// That order matters. A crash between the halves then leaves unreferenced files (invisible, only
    /// disk space) instead of entries pointing at missing files (a broken card, a wallpaper that cannot play).
    /// Files come from the manifest on disk, not the caller copy, so a variant generated since is removed too.
    public static LibraryManifest Delete(string root, IReadOnlySet<string> ids)
    {
        var doomed = new List<string>();
        var folders = new List<string>();
        var manifest = Update(root, m =>
        {
            // An interactive wallpaper is a whole folder (its page may have more files than the manifest names).
            folders.AddRange(m.Wallpapers.Where(w => ids.Contains(w.Id) && w.IsInteractive).Select(w => Catalog.HostedInteractive.FolderFor(root, w.Id)));
            doomed.AddRange(m.Wallpapers.Where(w => ids.Contains(w.Id)).SelectMany(w => w.RelativeFiles));
            m.Wallpapers.RemoveAll(w => ids.Contains(w.Id));
        });
        // The engine releases a deleted wallpaper a moment after the config changes, so a file can be
        // briefly in use. Retry for a couple of seconds; anything left is swept later.
        for (var attempt = 0; attempt < 5 && doomed.Count > 0; attempt++)
        {
            doomed.RemoveAll(relative =>
            {
                try { File.Delete(LibraryManifest.Resolve(root, relative)); return true; } catch { return false; }
            });
            if (doomed.Count > 0) Thread.Sleep(400);
        }
        foreach (var folder in folders)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { }
        }
        return manifest;
    }

    /// Deletes asset files no manifest entry references and returns the bytes freed. A file with no row
    /// yet may be one an import is still writing, so anything modified within the grace period is
    /// spared. Hidden names (partial downloads) are never touched.
    public static long SweepOrphans(string root, TimeSpan? grace = null)
    {
        // An unreadable manifest looks like an empty library, which would make every file an orphan.
        if (LibraryManifest.Read(root).Status != LibraryManifest.Status.Loaded) return 0;

        var cutoff = DateTime.UtcNow - (grace ?? TimeSpan.FromMinutes(15));
        long freed = 0;
        Update(root, manifest =>
        {
            var referenced = manifest.Wallpapers.SelectMany(w => w.RelativeFiles)
                .Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { "Masters", "Thumbnails", "Previews" })
            {
                var dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
                {
                    if (file.Name.StartsWith('.') || referenced.Contains(folder + "/" + file.Name)) continue;
                    if (file.LastWriteTimeUtc > cutoff) continue;
                    try { var size = file.Length; file.Delete(); freed += size; } catch { }
                }
            }
        });
        return freed;
    }

    private static IDisposable LockFile(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".library.lock");
        // Best effort, as on macOS: if the lock file cannot be opened the in-process lock still holds.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { break; }
        }
        return new NoLock();
    }

    private sealed class NoLock : IDisposable { public void Dispose() { } }
}
