using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;
using Muro.Platform;

namespace Muro.Engine;

/// Carries out one <c>muro://</c> link by writing config.json, the same way the CLI's set, pause and resume do; a
/// running engine picks the change up from the file. Links are done one at a time (a named mutex), so
/// <c>set</c> followed by <c>pause</c> ends paused. Safe by rule, because any web page can open a link:
///   - set works only for a wallpaper already in the library, and never starts a download;
///   - a display that is not connected is ignored;
///   - nothing is shown to the user, and a link that is ignored is written to the log with the reason.
/// shortcut: the macOS app drops links beyond 16 waiting; here each link is its own short process that waits up to
/// 10 seconds for its turn and is ignored after that, which bounds the queue the same way.
internal static class LinkHandler
{
    public static void Run(string root, string text)
    {
        using var turn = new Mutex(false, "Muro.Links");
        var held = false;
        try
        {
            try { held = turn.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; }   // the previous link's process died mid-way: safe to go on
            if (!held) { Log.Info("link ignored (too many waiting): " + text); return; }
            Apply(root, text);
        }
        finally { if (held) turn.ReleaseMutex(); }
    }

    private static void Apply(string root, string text)
    {
        if (EngineConfig.Load(root).LinksOff) { Log.Info("link ignored (links are off in Settings): " + text); return; }
        if (MuroLink.Parse(text) is not { } link) { Log.Info("link ignored (not a muro link Muro understands): " + text); return; }

        switch (link.Kind)
        {
            case MuroLinkKind.Pause: EngineConfig.Update(root, c => c.Paused = true); Log.Info("link: pause"); return;
            case MuroLinkKind.Resume: EngineConfig.Update(root, c => c.Paused = null); Log.Info("link: resume"); return;
        }

        var entry = LibraryManifest.Load(root).Find(link.Id!);
        if (entry is null) { Log.Info("link ignored (that wallpaper is not in your library): " + link.Id); return; }

        string? monitorId = null;
        if (link.Display is not null)
        {
            monitorId = Monitors.All().FirstOrDefault(m => m.Id == link.Display)?.Id;
            if (monitorId is null) { Log.Info("link ignored (that display is not connected): " + link.Display); return; }
        }

        var mode = AppSettings.Load(root).ModeFor(entry);
        var assignment = new EngineConfig.Assignment(entry.Id, mode);
        var desktop = link.Surface is "desktop" or "all";
        var lockScreen = link.Surface is "lockscreen" or "all";
        var saver = link.Surface is "screensaver" or "all";
        EngineConfig.Update(root, c =>
        {
            if (desktop) { if (monitorId is null) c.AllDisplays = assignment; else c.PerDisplay[monitorId] = assignment; }
            if (lockScreen) c.LockScreen = assignment;
            if (saver) c.ScreenSaver = assignment;
        });
        // Picking a wallpaper by hand ends a playlist or automation on that surface; a link does the same.
        if (desktop) ScheduleControl.Stop(root, Surface.Desktop, restore: false);
        if (lockScreen) ScheduleControl.Stop(root, Surface.LockScreen, restore: false);
        if (saver) ScheduleControl.Stop(root, Surface.ScreenSaver, restore: false);
        Log.Info($"link: set {entry.Title} ({link.Surface}{(monitorId is null ? "" : ", one display")})");
    }
}
