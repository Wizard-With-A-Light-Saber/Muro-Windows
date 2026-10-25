using System.Diagnostics;

namespace Muro.Core.Library;

/// Where Muro keeps its wallpaper videos.
///
/// By default they sit in Masters inside the library. A person can move them anywhere, an external
/// drive included: Muro makes a folder called "Muro Wallpapers" in the place they pick, moves the
/// videos into it, and turns Masters into a directory junction to it. Every path that reads
/// Masters/{id}.mov keeps working unchanged (engine, imports, downloads, the CLI), and the junction
/// is itself the setting, so there is nothing else to keep in step. Junctions need no admin rights,
/// unlike symbolic links.
///
/// Only the videos move. library.json, config.json, thumbnails and previews stay in the library on
/// this PC, so a drive that is not connected costs its wallpapers until it is back, never the settings.
///
/// A folder Muro did not make is never used. SweepOrphans deletes files in Masters that no wallpaper
/// refers to, so a link to a folder holding anything else would delete the person files. Muro own
/// folder carries a hidden marker, and a "Muro Wallpapers" folder without one is refused unless empty.
public static class DownloadFolder
{
    public const string FolderName = "Muro Wallpapers";
    private const string MarkerName = ".muro-wallpapers";

    public abstract record Location
    {
        /// The library own Masters folder.
        public sealed record BuiltIn : Location;
        /// Somewhere else, and there.
        public sealed record Custom(string Path) : Location;
        /// Somewhere else, and not there, like a drive that is not connected.
        public sealed record Unavailable(string Path) : Location;
    }

    public enum MoveError { NotAFolder, InsideLibrary, AlreadyThere, FolderInUse, NotConnected, NotEnoughSpace, CopyFailed, LinkFailed, Cancelled }

    public sealed class MoveException(MoveError error, string message) : IOException(message)
    {
        public MoveError Error { get; } = error;
    }

    public static string MastersPath(string root) => Path.Combine(root, "Masters");

    /// Read from the junction itself, so every process sees the same answer.
    public static Location GetLocation(string root)
    {
        string? target;
        try { target = new DirectoryInfo(MastersPath(root)).LinkTarget; }
        catch { return new Location.BuiltIn(); }
        if (target is null) return new Location.BuiltIn();
        if (target.StartsWith(@"\??\")) target = target[4..];
        return Directory.Exists(target) ? new Location.Custom(target) : new Location.Unavailable(target);
    }

    /// The name a person knows the place by: the drive for a folder on one, else the folder name.
    public static string PlaceName(string path)
    {
        var full = Path.GetFullPath(path);
        var rootName = Path.GetPathRoot(full) ?? "";
        var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(parent) || parent.Length <= rootName.Length ? rootName : Path.GetFileName(parent);
    }

    /// Moves every video into a "Muro Wallpapers" folder inside <paramref name="picked"/>, or back
    /// into the library when it is null.
    ///
    /// Nothing is deleted until every video has a checked copy in its new place and the junction
    /// points there. A failure before that removes only what this move made. A cancelled move throws
    /// <see cref="MoveError.Cancelled"/> before the junction is switched, so it changes nothing.
    /// Run it off the UI thread; callbacks arrive on the calling thread.
    public static void Move(
        string root, string? picked,
        Action<int, int>? progress = null, Action<long, long>? bytes = null, Func<bool>? isCancelled = null)
    {
        var masters = MastersPath(root);
        var current = GetLocation(root);
        string source;
        switch (current)
        {
            case Location.BuiltIn: source = masters; break;
            case Location.Custom custom: source = custom.Path; break;
            case Location.Unavailable gone:
                throw new MoveException(MoveError.NotConnected, "Connect " + PlaceName(gone.Path) + " first. Your wallpapers are on it.");
            default: return;
        }

        string destination;
        if (picked is not null)
        {
            if (!Directory.Exists(picked)) throw new MoveException(MoveError.NotAFolder, "That is not a folder Muro can use.");
            if (IsInside(picked, root)) throw new MoveException(MoveError.InsideLibrary, "Pick a folder outside the one Muro uses now.");
            destination = Path.Combine(picked, FolderName);
            if (current is Location.Custom && SamePlace(destination, source))
                throw new MoveException(MoveError.AlreadyThere, "Muro already keeps your wallpapers there.");
            if (IsInside(destination, source) || IsInside(source, destination))
                throw new MoveException(MoveError.InsideLibrary, "Pick a folder outside the one Muro uses now.");
            if (Directory.Exists(destination) && !(IsOurs(destination) || IsEmpty(destination)))
                throw new MoveException(MoveError.FolderInUse, "A folder called " + FolderName + " is already there and holds other files. Pick another place.");
        }
        else
        {
            if (current is not Location.Custom) return;
            destination = Path.Combine(root, "Masters.moving-" + Guid.NewGuid().ToString("N"));
        }

        var videos = FilesIn(source);
        var total = videos.Sum(v => v.Size);
        if (!SameVolume(source, destination))
        {
            var free = AvailableSpace(picked ?? root);
            // Room for the videos and a little over, so the move never fills the disk to the last byte.
            const long margin = 256L * 1024 * 1024;
            if (free >= 0 && free < total + margin)
                throw new MoveException(MoveError.NotEnoughSpace,
                    "There is not enough space there. Your wallpapers need " + FormatBytes(total) + " and " + FormatBytes(free) + " is free.");
        }

        var existed = Directory.Exists(destination);
        Directory.CreateDirectory(destination);
        var made = new List<string>();
        try
        {
            if (picked is not null)
            {
                var marker = Path.Combine(destination, MarkerName);
                if (!File.Exists(marker)) File.WriteAllText(marker, "Muro keeps its wallpaper videos here.\n");
            }
            progress?.Invoke(0, videos.Count);
            bytes?.Invoke(0, total);
            long copiedBefore = 0;
            for (var i = 0; i < videos.Count; i++)
            {
                if (isCancelled?.Invoke() == true) throw new MoveException(MoveError.Cancelled, "The move was cancelled. Your wallpapers stayed where they were.");
                var video = videos[i];
                var target = Path.Combine(destination, Path.GetFileName(video.Path));
                if (SizeOf(target) != video.Size)
                {
                    var before = copiedBefore;
                    Copy(video.Path, target, video.Size, done => bytes?.Invoke(before + done, total), isCancelled);
                    made.Add(target);
                }
                copiedBefore += video.Size;
                bytes?.Invoke(copiedBefore, total);
                progress?.Invoke(i + 1, videos.Count);
            }
            if (isCancelled?.Invoke() == true) throw new MoveException(MoveError.Cancelled, "The move was cancelled. Your wallpapers stayed where they were.");

            switch (current, picked)
            {
                case (Location.BuiltIn, not null):
                {
                    string? aside = null;
                    if (Directory.Exists(masters))
                    {
                        aside = Path.Combine(root, "Masters.old-" + Guid.NewGuid().ToString("N"));
                        Directory.Move(masters, aside);
                    }
                    try { CreateJunction(masters, destination); }
                    catch
                    {
                        if (aside is not null) Directory.Move(aside, masters);
                        throw;
                    }
                    if (aside is not null) FinishLeaving(aside, destination, removeFolder: true);
                    break;
                }
                case (Location.Custom, not null):
                {
                    // Windows cannot rename over a junction, so there is a brief moment with no Masters.
                    // The engine retries a missing video, so it costs nothing visible.
                    Directory.Delete(masters, recursive: false);
                    try { CreateJunction(masters, destination); }
                    catch
                    {
                        CreateJunction(masters, source);
                        throw;
                    }
                    FinishLeaving(source, destination, removeFolder: IsOurs(source));
                    break;
                }
                case (Location.Custom, null):
                {
                    Directory.Delete(masters, recursive: false);
                    try { Directory.Move(destination, masters); }
                    catch
                    {
                        CreateJunction(masters, source);
                        throw;
                    }
                    FinishLeaving(source, masters, removeFolder: IsOurs(source));
                    break;
                }
            }
        }
        catch
        {
            foreach (var path in made) { try { File.Delete(path); } catch { } }
            if (!existed) { try { Directory.Delete(destination, recursive: true); } catch { } }
            throw;
        }
    }

    // ---- Pieces

    /// Clears out the old folder once the new one is in use. A video that reached the old folder while
    /// the rest were copying (a download that finished then) is carried across rather than lost. A
    /// video that cannot be carried is left where it is, and so is the folder holding it.
    private static void FinishLeaving(string old, string next, bool removeFolder)
    {
        foreach (var video in FilesIn(old))
        {
            var target = Path.Combine(next, Path.GetFileName(video.Path));
            if (SizeOf(target) != video.Size)
            {
                try { Copy(video.Path, target, video.Size, null, null); } catch { continue; }
            }
            try { File.Delete(video.Path); } catch { }
        }
        foreach (var name in Directory.EnumerateFiles(old, "*.partial"))
        {
            try { File.Delete(name); } catch { }
        }
        if (!removeFolder) return;
        try { File.Delete(Path.Combine(old, MarkerName)); } catch { }
        if (IsEmpty(old, countingHidden: true)) { try { Directory.Delete(old); } catch { } }
    }

    /// Copied under a hidden name and renamed once the size checks out, so a copy cut short never
    /// sits under a video real name.
    private static void Copy(string file, string target, long expected, Action<long>? onBytes, Func<bool>? isCancelled)
    {
        var partial = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + ".partial");
        try { File.Delete(partial); } catch { }
        try
        {
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (isCancelled?.Invoke() == true) throw new MoveException(MoveError.Cancelled, "The move was cancelled. Your wallpapers stayed where they were.");
                    output.Write(buffer, 0, read);
                    done += read;
                    onBytes?.Invoke(done);
                }
            }
            if (SizeOf(partial) != expected) throw new MoveException(MoveError.CopyFailed, Path.GetFileName(file) + " could not be copied. Nothing was moved.");
            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void CreateJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info) ?? throw new MoveException(MoveError.LinkFailed, "Could not link the wallpaper folder.");
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new MoveException(MoveError.LinkFailed, "Could not link the wallpaper folder. " + error.Trim());
    }

    /// The videos in a folder: visible regular files with their sizes.
    private static List<(string Path, long Size)> FilesIn(string folder) =>
        !Directory.Exists(folder)
            ? []
            : new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => !f.Name.StartsWith('.'))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => (f.FullName, f.Length)).ToList();

    private static long? SizeOf(string path)
    {
        try { return new FileInfo(path) is { Exists: true } f ? f.Length : null; } catch { return null; }
    }

    private static bool IsOurs(string folder) => File.Exists(Path.Combine(folder, MarkerName));

    private static bool IsEmpty(string folder, bool countingHidden = false) =>
        Directory.EnumerateFileSystemEntries(folder).All(p => !countingHidden && Path.GetFileName(p).StartsWith('.'));

    private static string Canonical(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePlace(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    /// True when inner is outer or anywhere under it.
    public static bool IsInside(string inner, string outer)
    {
        var a = Canonical(inner);
        var b = Canonical(outer);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// Same disk, so a copy needs no free space check. Compared by drive root.
    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// Free space where the folder will go, or -1 when the disk will not say (network shares).
    private static long AvailableSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; } catch { return -1; }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0") + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0") + " MB",
        _ => (bytes / 1024.0).ToString("0") + " KB",
    };
}
