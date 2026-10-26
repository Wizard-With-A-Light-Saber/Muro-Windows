using Muro.Core;
using Muro.Core.Media;
using Muro.Platform;

namespace Muro.Engine;

/// Keeps the Windows lock screen picture in step with config.json. Windows cannot play video on the lock
/// screen, so it shows one still frame of the chosen wallpaper. The first time, the picture that was there is
/// saved, and when the lock screen assignment goes away it is put back, so turning this off leaves the user's
/// own picture as it was. Work runs off the message loop (ffmpeg and the lock screen API both block).
internal sealed class LockScreenService
{
    private readonly string _root;
    private readonly SystemEvents _events;
    private bool _busy, _again;
    private string? _shown;

    public LockScreenService(string root, SystemEvents events)
    {
        _root = root;
        _events = events;
        _shown = ReadMarker();
    }

    private string OriginalPath => Path.Combine(_root, "Stills", "lockscreen-original.jpg");
    private string MarkerPath => Path.Combine(_root, "Stills", "lockscreen-active.txt");

    private string? ReadMarker()
    {
        try { return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// Call on the loop thread whenever the config may have changed.
    public void Sync(EngineConfig config)
    {
        var wanted = config.LockScreen?.WallpaperID;
        if (wanted == _shown) return;
        if (_busy) { _again = true; return; }
        _busy = true;
        Task.Run(() => Apply(wanted)).ContinueWith(t =>
        {
            if (t.Exception is { } ex) Log.Info($"lock screen: {ex.GetBaseException().Message}");
            _events.Post(() =>
            {
                _busy = false;
                if (_again) { _again = false; Sync(EngineConfig.Load(_root)); }
            });
        });
    }

    private async Task Apply(string? wanted)
    {
        if (wanted is null)
        {
            // Back to the picture the user had.
            if (File.Exists(OriginalPath)) LockScreenImage.Set(OriginalPath);
            File.Delete(MarkerPath);
            _shown = null;
            Log.Info("lock screen: original picture restored");
            return;
        }

        var ffmpeg = Ffmpeg.Locate();
        if (ffmpeg is null) { Log.Info("lock screen: ffmpeg not found, nothing changed"); return; }
        var still = await Stills.EnsureAsync(ffmpeg, _root, wanted);
        if (still is null) { Log.Info("lock screen: no frame could be made for " + wanted); return; }

        // Keep the user's own picture, once: only while nothing of ours is on the lock screen.
        if (_shown is null && !File.Exists(MarkerPath) && !LockScreenImage.SaveCurrent(OriginalPath))
            Log.Info("lock screen: there was no picture to save");
        if (LockScreenImage.Mode() != "picture") Log.Info("lock screen: was set to " + LockScreenImage.Mode() + ", now a picture");

        LockScreenImage.Set(still);
        File.WriteAllText(MarkerPath, wanted);
        _shown = wanted;
        Log.Info("lock screen: showing " + wanted);
    }
}
