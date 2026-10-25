namespace Muro.Core.Library;

/// Removes wallpapers and everything that points at them. The order is the macOS one and it matters:
/// 1. Off the desktop first (config), so the engine drops its layer while the file still exists.
/// 2. The manifest and then the files.
/// 3. Every list that refers to an id (playlists, automations, lock screen) through the callers sinks,
///    so no list is left rotating through a wallpaper that is gone.
public static class WallpaperDeletion
{
    public sealed record Result(bool ClearedDesktop, LibraryManifest Manifest);

    public static Result Delete(
        string root, IReadOnlySet<string> ids, IEnumerable<Action<IReadOnlySet<string>>>? sinks = null)
    {
        var config = EngineConfig.Load(root);
        var changed = false;
        if (config.AllDisplays is { } all && ids.Contains(all.WallpaperID))
        {
            config.AllDisplays = null;
            changed = true;
        }
        foreach (var key in config.PerDisplay.Where(kv => ids.Contains(kv.Value.WallpaperID)).Select(kv => kv.Key).ToList())
        {
            config.PerDisplay.Remove(key);
            changed = true;
        }
        if (changed) config.Save(root);

        var manifest = LibraryWriter.Delete(root, ids);
        foreach (var sink in sinks ?? []) sink(ids);
        return new Result(changed, manifest);
    }
}
