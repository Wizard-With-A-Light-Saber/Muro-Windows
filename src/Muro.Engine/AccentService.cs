using System.Text.Json;
using Muro.Core;
using Muro.Core.Accent;
using Muro.Core.Library;
using Muro.Core.Media;
using Muro.Platform;

namespace Muro.Engine;

/// "Match the accent colour to the wallpaper": while it is on, the Windows accent colour is the dominant colour
/// of the wallpaper on the main display, and changes whenever that wallpaper does (a playlist step, a new
/// apply). The first time, the user's own accent settings are saved to accent-backup.json, and when the switch
/// goes off they are put back. The picture comes from the wallpaper's thumbnail; the work runs off the loop thread.
internal sealed class AccentService
{
    private readonly string _root;
    private readonly SystemEvents _events;
    private bool _busy, _again;
    private string? _appliedFor;

    public AccentService(string root, SystemEvents events)
    {
        _root = root;
        _events = events;
    }

    private string BackupPath => Path.Combine(_root, "accent-backup.json");

    private static string? WantedId(EngineConfig config)
    {
        var main = Monitors.All().FirstOrDefault(m => m.Primary);
        return config.AssignmentFor(main?.Id)?.WallpaperID;
    }

    /// Call on the loop thread whenever the config may have changed.
    public void Sync(EngineConfig config)
    {
        var on = config.MatchAccent;
        var wanted = on ? WantedId(config) : null;
        var nothingToDo = on ? wanted is null || wanted == _appliedFor : !File.Exists(BackupPath);
        if (nothingToDo) return;
        if (_busy) { _again = true; return; }
        _busy = true;
        Task.Run(async () => { if (on) await Apply(wanted!); else Restore(); }).ContinueWith(t =>
        {
            if (t.Exception is { } ex) Log.Info($"accent: {ex.GetBaseException().Message}");
            _events.Post(() =>
            {
                _busy = false;
                if (_again) { _again = false; Sync(EngineConfig.Load(_root)); }
            });
        });
    }

    private async Task Apply(string wallpaperId)
    {
        if (LibraryManifest.Load(_root).Find(wallpaperId) is not { Thumbnail.Length: > 0 } entry) return;
        var thumbnail = LibraryManifest.Resolve(_root, entry.Thumbnail);
        var ffmpeg = Ffmpeg.Locate();
        if (!File.Exists(thumbnail) || ffmpeg is null) { Log.Info("accent: no picture or no ffmpeg, nothing changed"); return; }
        var sample = await Stills.SampleAsync(ffmpeg, thumbnail);
        if (sample is null) { Log.Info("accent: could not read the wallpaper picture"); return; }

        // Keep the user's own colour, once.
        if (!File.Exists(BackupPath)) File.WriteAllText(BackupPath, JsonSerializer.Serialize(AccentColor.Read()));
        var color = AccentPicker.Pick(sample);
        AccentColor.Apply(color);
        _appliedFor = wallpaperId;
        Log.Info($"accent: #{color.R:X2}{color.G:X2}{color.B:X2} from {entry.Title}");
        AccentColor.Broadcast();
    }

    private void Restore()
    {
        try
        {
            if (JsonSerializer.Deserialize<AccentBackup>(File.ReadAllText(BackupPath)) is { } backup) AccentColor.Restore(backup);
            File.Delete(BackupPath);
            _appliedFor = null;
            Log.Info("accent: original colour restored");
            AccentColor.Broadcast();
        }
        catch (JsonException) { File.Delete(BackupPath); }   // a damaged backup cannot be used: do not retry forever
    }
}
