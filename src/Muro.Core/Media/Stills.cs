using System.Globalization;
using Muro.Core.Library;

namespace Muro.Core.Media;

/// Full-size stills for the lock screen. Windows cannot play video there, so it shows one frame of the
/// wallpaper. Kept under Stills\ in the library and made once per wallpaper.
public static class Stills
{
    public static string PathFor(string root, string wallpaperId) => Path.Combine(root, "Stills", wallpaperId + ".jpg");

    /// The still for a wallpaper, made from the middle of its first seconds at up to 3840 wide, or null when
    /// the wallpaper or its video is missing or ffmpeg fails.
    public static async Task<string?> EnsureAsync(string ffmpeg, string root, string wallpaperId, CancellationToken ct = default)
    {
        var output = PathFor(root, wallpaperId);
        if (File.Exists(output)) return output;
        if (LibraryManifest.Load(root).Find(wallpaperId) is not { } entry) return null;
        var video = LibraryManifest.Resolve(root, entry.File);
        if (!File.Exists(video)) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var tmp = output + ".part.jpg";
        var at = Math.Min(2.0, Math.Max(0, entry.Duration / 2)).ToString("0.###", CultureInfo.InvariantCulture);
        var (code, _) = await Ffmpeg.RunAsync(ffmpeg,
        [
            "-y", "-hide_banner", "-loglevel", "error", "-ss", at, "-i", video,
            "-frames:v", "1", "-vf", "scale=min(3840\\,iw):-2", "-q:v", "2", tmp,
        ], TimeSpan.FromSeconds(60), null, 0, ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } return null; }
        File.Move(tmp, output, overwrite: true);
        return output;
    }
}
