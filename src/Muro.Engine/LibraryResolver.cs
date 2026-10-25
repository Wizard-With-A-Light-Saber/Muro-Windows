using Muro.Core;
using Muro.Core.Library;

namespace Muro.Engine;

/// Turns a config assignment into the video file to play.
internal static class LibraryResolver
{
    /// <paramref name="IsEfficientFile"/> is true when the file is the 30 fps variant, so the player
    /// needs no frame-rate filter on top.
    public readonly record struct Resolved(string Path, bool IsEfficientFile);

    public static Resolved? Resolve(string root, EngineConfig.Assignment assignment)
    {
        // A plain file path is accepted so the engine can be tried without a library.
        if (File.Exists(assignment.WallpaperID)) return new Resolved(assignment.WallpaperID, false);

        var entry = LibraryManifest.Load(root).Find(assignment.WallpaperID);
        if (entry is null) return null;

        if (assignment.IsEfficient && entry.EfficientFile is { } variant)
        {
            var variantPath = LibraryManifest.Resolve(root, variant);
            if (File.Exists(variantPath)) return new Resolved(variantPath, true);
        }

        var master = LibraryManifest.Resolve(root, entry.File);
        // A missing master is a drive that is not connected (or a file removed outside Muro): report
        // nothing to play rather than hand the player a path that cannot open.
        return File.Exists(master) ? new Resolved(master, false) : null;
    }
}
