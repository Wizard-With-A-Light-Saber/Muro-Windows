using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Muro.Core.Media;

public sealed record VideoInfo(string Codec, int Width, int Height, double Fps, double Duration);

public sealed class TranscodeException(string message) : Exception(message);

/// Finding, probing and running ffmpeg. Muro bundles an LGPL ffmpeg build and runs it as a separate
/// process, so Muro own code stays out of ffmpeg licence scope.
public static partial class Ffmpeg
{
    /// Environment override, then next to the app, then the dev tree (third_party), then PATH.
    public static string? Locate()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MURO_FFMPEG");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"), Path.Combine(baseDir, "ffmpeg.exe") })
            if (File.Exists(candidate)) return candidate;

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var dev = Path.Combine(dir.FullName, "third_party", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(dev)) return dev;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var onPath = Path.Combine(folder.Trim(), "ffmpeg.exe");
                if (File.Exists(onPath)) return onPath;
            }
            catch { }
        }
        return null;
    }

    /// Reads what ffmpeg prints for "-i file" (it exits non-zero without an output, which is fine).
    /// Rotation metadata swaps width and height, since ffmpeg auto-rotates on output.
    public static VideoInfo? ParseProbe(string stderr)
    {
        var video = stderr.Split('\n').FirstOrDefault(l => l.Contains(" Video: ") && !l.Contains("attached pic"));
        if (video is null) return null;

        var codec = CodecRegex().Match(video);
        var size = SizeRegex().Match(video);
        if (!codec.Success || !size.Success) return null;
        var width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);

        var fpsMatch = FpsRegex().Match(video);
        var tbrMatch = TbrRegex().Match(video);
        var fps = fpsMatch.Success ? Parse(fpsMatch.Groups[1].Value) : tbrMatch.Success ? Parse(tbrMatch.Groups[1].Value) : 0;

        double duration = 0;
        var d = DurationRegex().Match(stderr);
        if (d.Success) duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + Parse(d.Groups[3].Value);

        var rotation = RotationRegex().Match(stderr);
        if (rotation.Success && Math.Abs(Math.Round(Parse(rotation.Groups[1].Value)) % 180) == 90)
            (width, height) = (height, width);

        return new VideoInfo(codec.Groups[1].Value, width, height, fps, duration);
    }

    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public static async Task<VideoInfo?> ProbeAsync(string ffmpeg, string path, CancellationToken ct = default)
    {
        var (_, stderr) = await RunAsync(ffmpeg, ["-hide_banner", "-i", path], TimeSpan.FromSeconds(30), null, 0, ct).ConfigureAwait(false);
        return ParseProbe(stderr);
    }

    /// Runs ffmpeg and returns its exit code and stderr. When <paramref name="progress"/> is given the
    /// arguments must include "-progress pipe:1"; the reported value is encoded seconds over duration.
    /// Cancelling kills the whole process tree.
    public static async Task<(int ExitCode, string Stderr)> RunAsync(
        string ffmpeg, IEnumerable<string> args, TimeSpan? timeout, IProgress<double>? progress, double durationSeconds, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var process = new Process { StartInfo = info };
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        double reported = 0;
        process.OutputDataReceived += (_, e) =>
        {
            if (progress is null || durationSeconds <= 0 || e.Data is null || !e.Data.StartsWith("out_time_us=")) return;
            if (!long.TryParse(e.Data.AsSpan("out_time_us=".Length), out var us) || us < 0) return;
            var done = Math.Min(1.0, us / 1e6 / durationSeconds);
            if (done - reported >= 0.01) { reported = done; progress.Report(done); }
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) linked.CancelAfter(t);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var kill = linked.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { process.WaitForExit(); }

        ct.ThrowIfCancellationRequested();
        if (linked.IsCancellationRequested) throw new TranscodeException("ffmpeg timed out");
        lock (stderr) return (process.ExitCode, stderr.ToString());
    }

    [GeneratedRegex(@"Video: (\w+)")] private static partial Regex CodecRegex();
    [GeneratedRegex(@", (\d{2,5})x(\d{2,5})")] private static partial Regex SizeRegex();
    [GeneratedRegex(@"(\d+(?:\.\d+)?) fps")] private static partial Regex FpsRegex();
    [GeneratedRegex(@"(\d+(?:\.\d+)?) tbr")] private static partial Regex TbrRegex();
    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")] private static partial Regex DurationRegex();
    [GeneratedRegex(@"rotation of (-?\d+(?:\.\d+)?) degrees")] private static partial Regex RotationRegex();
}
