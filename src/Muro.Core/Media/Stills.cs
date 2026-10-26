using System.Globalization;
using Muro.Core.Library;

namespace Muro.Core.Media;

/// Full-size stills for the lock screen. Windows cannot play video there, so it shows one frame of the
/// wallpaper. Kept under Stills\ in the library and made once per wallpaper.
public static class Stills
{
    public static string PathFor(string root, string wallpaperId) => Path.Combine(root, "Stills", wallpaperId + ".jpg");

    /// A 64x36 RGB24 copy of a picture (3 bytes per pixel), small enough to read a dominant colour from. Null when
    /// ffmpeg cannot read the picture.
    public static async Task<byte[]?> SampleAsync(string ffmpeg, string picture, CancellationToken ct = default)
    {
        var raw = Path.Combine(Path.GetTempPath(), "muro-sample-" + Guid.NewGuid().ToString("N")[..8] + ".raw");
        try
        {
            var (code, _) = await Ffmpeg.RunAsync(ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-i", picture,
                "-vf", "scale=64:36", "-pix_fmt", "rgb24", "-f", "rawvideo", raw,
            ], TimeSpan.FromSeconds(30), null, 0, ct).ConfigureAwait(false);
            return code == 0 && File.Exists(raw) && new FileInfo(raw).Length == 64 * 36 * 3 ? await File.ReadAllBytesAsync(raw, ct) : null;
        }
        finally { try { File.Delete(raw); } catch (IOException) { } }
    }

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
