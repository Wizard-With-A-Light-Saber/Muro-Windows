using System.Globalization;

namespace Muro.Core.Media;

public sealed record EncoderChoice(string Name, string Description, bool Hardware, string Codec);

/// Which video encoders actually work on this machine. Being listed in "ffmpeg -encoders" proves
/// nothing (NVENC is compiled in on a PC with no NVIDIA card), so each candidate does a real
/// half-second test encode. Hardware first, then software.
public static class Encoders
{
    private static readonly EncoderChoice[] Candidates =
    [
        new("hevc_nvenc", "NVIDIA NVENC (HEVC)", true, "hevc"),
        new("hevc_qsv", "Intel Quick Sync (HEVC)", true, "hevc"),
        new("hevc_amf", "AMD AMF (HEVC)", true, "hevc"),
        // The LGPL build has no x264/x265. SVT-AV1 is the free software fallback; mpv plays AV1
        // in software if the GPU cannot, and the wallpaper loops are short.
        new("libsvtav1", "SVT-AV1 (software)", false, "av1"),
    ];

    private static IReadOnlyList<EncoderChoice>? _cached;

    public static async Task<IReadOnlyList<EncoderChoice>> DetectAsync(string ffmpeg, CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var working = new List<EncoderChoice>();
        foreach (var candidate in Candidates)
        {
            try
            {
                var (code, _) = await Ffmpeg.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                    "-t", "0.5", "-c:v", candidate.Name, "-pix_fmt", "yuv420p", "-f", "null", "-",
                ], TimeSpan.FromSeconds(30), null, 0, ct).ConfigureAwait(false);
                if (code == 0) working.Add(candidate);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return _cached = working;
    }
}

public static class Transcoder
{
    /// About 0.045 bits per pixel per frame, clamped: 4K30 is about 11 Mbps, 1080p30 about 3 Mbps.
    public static int RecommendedBitrate(int width, int height, double fps) =>
        (int)Math.Clamp(width * (double)height * fps * 0.045, 3_000_000, 25_000_000);

    /// The ffmpeg command line for one encode. Pure, so it is tested without ffmpeg.
    public static List<string> BuildArgs(EncoderChoice encoder, string input, string output, VideoInfo info, bool halveFps, (int Width, int Height)? scaleTo = null, string? lookFilter = null, bool keepAudio = false)
    {
        var outFps = halveFps ? info.Fps / 2 : info.Fps;
        var (outWidth, outHeight) = scaleTo ?? (info.Width, info.Height);
        var bitrate = RecommendedBitrate(outWidth, outHeight, outFps);
        var gop = Math.Max(1, (int)Math.Round(outFps) * 2);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-nostats", "-loglevel", "error", "-progress", "pipe:1",
            "-i", input, "-map", "0:v:0",
        };
        // A wallpaper is silent unless the user turned Live Wallpaper Sound on before importing it.
        if (keepAudio) args.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-b:a", "160k"]);
        else args.Add("-an");
        args.AddRange(["-sn", "-dn"]);
        // Dropping every second frame is exact for 2:1 sources (60 to 30, 59.94 to 29.97).
        // One -vf: ffmpeg keeps only the last one given.
        var filters = new List<string>();
        if (halveFps) filters.Add("fps=" + outFps.ToString("0.###", CultureInfo.InvariantCulture));
        if (scaleTo is { } size) filters.Add($"scale={size.Width}:{size.Height}:flags=lanczos");
        if (lookFilter is not null) filters.Add(lookFilter);   // after the scale, so it runs on fewer pixels
        if (filters.Count > 0) { args.Add("-vf"); args.Add(string.Join(",", filters)); }
        args.AddRange(["-pix_fmt", "yuv420p", "-c:v", encoder.Name]);

        var kbps = (bitrate / 1000).ToString(CultureInfo.InvariantCulture);
        var peak = (bitrate * 3 / 2000).ToString(CultureInfo.InvariantCulture);
        var buffer = (bitrate / 500).ToString(CultureInfo.InvariantCulture);
        switch (encoder.Name)
        {
            case "hevc_nvenc": args.AddRange(["-preset", "p5", "-rc", "vbr", "-b:v", kbps + "k", "-maxrate", peak + "k", "-bufsize", buffer + "k"]); break;
            case "hevc_qsv": args.AddRange(["-preset", "medium", "-b:v", kbps + "k", "-maxrate", peak + "k"]); break;
            case "hevc_amf": args.AddRange(["-quality", "quality", "-rc", "vbr_peak", "-b:v", kbps + "k", "-maxrate", peak + "k"]); break;
            default: args.AddRange(["-preset", "8", "-crf", "32"]); break;
        }
        args.AddRange(["-g", gop.ToString(CultureInfo.InvariantCulture)]);
        // hvc1 is the tag Windows and Apple players expect for HEVC in MOV.
        if (encoder.Codec == "hevc") args.AddRange(["-tag:v", "hvc1"]);
        // Explicit container: the output is written under a hidden .partial name first.
        args.AddRange(["-f", "mov", "-movflags", "+faststart", output]);
        return args;
    }

    /// Re-encodes video only (a wallpaper is silent) with the best working encoder, falling back down
    /// the list when one fails at run time (a driver that lists an encoder it cannot start).
    /// Returns what the output is. The caller owns the output path and removes it on failure.
    public static async Task<VideoInfo> TranscodeAsync(
        string ffmpeg, string input, string output, VideoInfo info, bool halveFps,
        IProgress<double>? progress, CancellationToken ct, (int Width, int Height)? scaleTo = null, string? lookFilter = null, bool keepAudio = false)
    {
        var encoders = await Encoders.DetectAsync(ffmpeg, ct).ConfigureAwait(false);
        if (encoders.Count == 0) throw new TranscodeException("No video encoder works on this PC, so the video cannot be converted.");

        var failures = new List<string>();
        foreach (var encoder in encoders)
        {
            try { File.Delete(output); } catch { }
            var (code, stderr) = await Ffmpeg.RunAsync(ffmpeg, BuildArgs(encoder, input, output, info, halveFps, scaleTo, lookFilter, keepAudio),
                null, progress, info.Duration, ct).ConfigureAwait(false);
            if (code == 0 && File.Exists(output))
                return await Ffmpeg.ProbeAsync(ffmpeg, output, ct).ConfigureAwait(false)
                       ?? info with { Fps = halveFps ? info.Fps / 2 : info.Fps, Width = scaleTo?.Width ?? info.Width, Height = scaleTo?.Height ?? info.Height };
            failures.Add(encoder.Name + ": " + LastLine(stderr));
        }
        try { File.Delete(output); } catch { }
        throw new TranscodeException("The video could not be converted. " + string.Join("; ", failures));
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";

    /// A still from the middle of the first second (or the middle of a shorter clip), 1280 wide at most.
    public static async Task<bool> ThumbnailAsync(string ffmpeg, string video, string output, double duration, CancellationToken ct)
    {
        var at = Math.Min(1.0, duration / 2).ToString("0.###", CultureInfo.InvariantCulture);
        var (code, _) = await Ffmpeg.RunAsync(ffmpeg,
        [
            "-y", "-hide_banner", "-loglevel", "error", "-ss", at, "-i", video,
            "-frames:v", "1", "-vf", "scale=min(1280\\,iw):-2", "-q:v", "3", output,
        ], TimeSpan.FromSeconds(60), null, 0, ct).ConfigureAwait(false);
        return code == 0 && File.Exists(output);
    }
}
