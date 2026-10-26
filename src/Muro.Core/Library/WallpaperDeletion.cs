using Muro.Core.Schedule;

namespace Muro.Core.Library;

/// Removes wallpapers and everything that points at them. The order matters:
/// 1. Playlists, automations and the running schedule first, so the scheduler can never pick a wallpaper
///    that is going. A schedule that ends up empty is stopped; one whose current wallpaper is deleted
///    moves on to its next valid one on its own (the engine notices the lists changed).
/// 2. Off the desktop (config), so the engine drops its layer while the file still exists.
/// 3. The manifest and then the files.
/// Extra sinks let a caller prune further lists (the lock screen later).
public static class WallpaperDeletion
{
    public sealed record Result(bool ClearedDesktop, LibraryManifest Manifest, IReadOnlyList<string> StoppedSchedules);

    public static Result Delete(
        string root, IReadOnlySet<string> ids, IEnumerable<Action<IReadOnlySet<string>>>? sinks = null)
    {
        var stopped = PruneSchedules(root, ids);

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
        // The screen saver follows the desktop when it has no wallpaper of its own; the lock screen goes back
        // to its original picture (the engine restores it when the assignment disappears).
        if (config.ScreenSaver is { } saver && ids.Contains(saver.WallpaperID)) { config.ScreenSaver = null; changed = true; }
        if (config.LockScreen is { } lockScreen && ids.Contains(lockScreen.WallpaperID)) { config.LockScreen = null; changed = true; }
        if (changed) config.Save(root);

        var manifest = LibraryWriter.Delete(root, ids);
        foreach (var id in ids)
        {
            try { File.Delete(Media.Stills.PathFor(root, id)); }   // its lock screen still, if one was made
            catch (IOException) { }
        }
        foreach (var sink in sinks ?? []) sink(ids);
        return new Result(changed, manifest, stopped);
    }

    /// Takes the ids out of every playlist and automation, and stops any running schedule that is left
    /// with nothing to play. Returns the names of the schedules it stopped.
    private static List<string> PruneSchedules(string root, IReadOnlySet<string> ids)
    {
        var set = ids.ToHashSet();
        var stopped = new List<string>();

        var playlists = PlaylistStore.Load(root);
        var (prunedPlaylists, emptiedPlaylists) = PlaylistStore.Pruned(playlists, set);
        if (!prunedPlaylists.SequenceEqual(playlists)) PlaylistStore.Save(root, prunedPlaylists);

        var automations = AutomationStore.Load(root);
        var (prunedAutomations, emptiedAutomations) = AutomationStore.Pruned(automations, set);
        if (!prunedAutomations.SequenceEqual(automations)) AutomationStore.Save(root, prunedAutomations);

        foreach (var surface in Enum.GetValues<Surface>())
        {
            var state = ScheduleStore.Load(root, surface);
            if (!state.IsActive) continue;
            var emptied = (state.PlaylistID is { } p && emptiedPlaylists.Contains(p))
                          || (state.AutomationID is { } a && emptiedAutomations.Contains(a));
            if (!emptied) continue;
            stopped.Add(playlists.FirstOrDefault(x => x.Id == state.PlaylistID)?.Name
                        ?? automations.FirstOrDefault(x => x.Id == state.AutomationID)?.Name ?? "A schedule");
            ScheduleControl.Stop(root, surface, restore: false);
        }
        return stopped;
    }
}
