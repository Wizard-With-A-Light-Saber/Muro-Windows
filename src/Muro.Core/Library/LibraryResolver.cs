namespace Muro.Core.Library;

/// Turns a config assignment into the video file to play. Shared by the engine (the desktop) and the
/// screen saver, so both pick exactly the same file for the same assignment.
public static class LibraryResolver
{
    /// <paramref name="IsEfficientFile"/> is true when the file is the 30 fps variant, so the player
    /// needs no frame-rate filter on top.
    public readonly record struct Resolved(string Path, bool IsEfficientFile, int? PauseAfterSeconds = null, EffectsConfig? Effects = null, bool HasSound = false);

    public static Resolved? Resolve(string root, EngineConfig.Assignment assignment)
    {
        // A plain file path is accepted so the engine can be tried without a library.
        if (File.Exists(assignment.WallpaperID)) return new Resolved(assignment.WallpaperID, false);

        var entry = LibraryManifest.Load(root).Find(assignment.WallpaperID);
        if (entry is null) return null;

        // A baked look lives only in the screen-sized copy, so it plays that copy in every quality mode. Until the copy
        // is made (or if its file is gone) the plain wallpaper plays.
        var lookBaked = entry.ActiveLook is not null && entry.BakedLook == entry.ActiveLook;
        if (!entry.HasSound && (assignment.IsEfficient || lookBaked) && entry.EfficientFile is { } variant)
        {
            var variantPath = LibraryManifest.Resolve(root, variant);
            if (File.Exists(variantPath)) return new Resolved(variantPath, true, entry.PauseAfterSeconds, entry.Effects);
        }

        var master = LibraryManifest.Resolve(root, entry.File);
        // A missing master is a drive that is not connected (or a file removed outside Muro): report
        // nothing to play rather than hand the player a path that cannot open.
        return File.Exists(master) ? new Resolved(master, false, entry.PauseAfterSeconds, entry.Effects, entry.HasSound) : null;
    }
}
