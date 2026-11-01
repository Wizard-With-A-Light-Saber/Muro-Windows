using Muro.Core.Library;
using Muro.Core.Media;

namespace Muro.Core.Tests;

public class MediaTests
{
    // What ffmpeg prints for "-i wind.mov", from a real catalog wallpaper.
    private const string Hevc4K = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'wind.mov':
          Duration: 00:00:01.92, start: 0.000000, bitrate: 12747 kb/s
          Stream #0:0[0x1](und): Video: hevc (Main) (hvc1 / 0x31637668), yuv420p(tv, bt709), 3840x2160, 12736 kb/s, 60 fps, 60 tbr, 600 tbn (default)
        """;

    [Fact]
    public void Probe_ReadsCodecSizeFpsAndDuration_NotTheHexInTheCodecTag()
    {
        var info = Ffmpeg.ParseProbe(Hevc4K)!;
        Assert.Equal(("hevc", 3840, 2160, 60.0, 1.92), (info.Codec, info.Width, info.Height, info.Fps, info.Duration));
    }

    [Fact]
    public void Probe_SwapsWidthAndHeightForRotatedVideo()
    {
        var info = Ffmpeg.ParseProbe("""
              Duration: 00:01:02.50, start: 0.0, bitrate: 9000 kb/s
              Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, 9000 kb/s, 29.97 fps, 29.97 tbr, 90k tbn
                Side data:
                  displaymatrix: rotation of -90.00 degrees
            """)!;
        Assert.Equal((1080, 1920, 62.5), (info.Width, info.Height, info.Duration));
        Assert.Equal(29.97, info.Fps);
    }

    [Fact]
    public void Probe_SkipsCoverArt_AndReturnsNullWhenThereIsNoVideo()
    {
        Assert.Null(Ffmpeg.ParseProbe("Stream #0:0: Audio: aac, 44100 Hz, stereo"));
        Assert.Null(Ffmpeg.ParseProbe("Stream #0:1: Video: mjpeg, yuvj420p, 600x600, 90k tbr (attached pic)"));
    }

    [Fact]
    public void Bitrate_FollowsPixelsAndRate_WithinTheClamp()
    {
        Assert.Equal(22_394_880, Transcoder.RecommendedBitrate(3840, 2160, 60));   // 4K60 is under the ceiling
        Assert.Equal(25_000_000, Transcoder.RecommendedBitrate(7680, 4320, 60));   // clamped high
        Assert.Equal(3_000_000, Transcoder.RecommendedBitrate(1280, 720, 30));     // clamped low
        Assert.InRange(Transcoder.RecommendedBitrate(1920, 1080, 60), 5_000_000, 6_000_000);
    }

    [Fact]
    public void Args_HevcGetsTheHvc1Tag_Av1DoesNot_AndBothDropAudio()
    {
        var info = new VideoInfo("h264", 1920, 1080, 30, 10);
        var hevc = Transcoder.BuildArgs(new("hevc_nvenc", "", true, "hevc"), "in.mp4", "out.mov", info, false);
        var av1 = Transcoder.BuildArgs(new("libsvtav1", "", false, "av1"), "in.mp4", "out.mov", info, false);

        Assert.Contains("hvc1", hevc);
        Assert.DoesNotContain("hvc1", av1);
        Assert.Contains("-an", hevc);
        Assert.Contains("-an", av1);
        Assert.Equal("out.mov", hevc[^1]);
        Assert.Contains("+faststart", hevc);
        Assert.DoesNotContain("-vf", hevc);
    }

    [Fact]
    public void Args_Efficient_HalvesTheFrameRate_ExactlyForNtscRates()
    {
        var args = Transcoder.BuildArgs(new("hevc_qsv", "", true, "hevc"), "in.mov", "out.mov", new VideoInfo("hevc", 1920, 1080, 59.94, 10), true);
        Assert.Equal("fps=29.97", args[args.IndexOf("-vf") + 1]);
        Assert.Equal("60", args[args.IndexOf("-g") + 1]);   // two seconds of 29.97
    }

    [Fact]
    public void Args_Efficient_ScalesInTheSameFilterAsTheFrameRate_AndBitrateFollowsTheOutputSize()
    {
        var encoder = new EncoderChoice("hevc_qsv", "", true, "hevc");
        var info = new VideoInfo("hevc", 3840, 2160, 25, 10);

        var scaled = Transcoder.BuildArgs(encoder, "in.mov", "out.mov", info, false, (1920, 1080));
        Assert.Equal("scale=1920:1080:flags=lanczos", scaled[scaled.IndexOf("-vf") + 1]);   // 25 fps is kept
        Assert.Equal(1, scaled.Count(a => a == "-vf"));
        Assert.Equal((Transcoder.RecommendedBitrate(1920, 1080, 25) / 1000) + "k", scaled[scaled.IndexOf("-b:v") + 1]);

        var both = Transcoder.BuildArgs(encoder, "in.mov", "out.mov", info with { Fps = 60 }, true, (1920, 1080));
        Assert.Equal("fps=30,scale=1920:1080:flags=lanczos", both[both.IndexOf("-vf") + 1]);
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080, 1920, 1080)]   // 4K on a 1080p screen
    [InlineData(2560, 1440, 1920, 1080, 1920, 1080)]   // 1440p on a 1080p screen
    [InlineData(3840, 1600, 1920, 1080, 2592, 1080)]   // ultrawide covers the height, the player crops the sides
    public void TargetSize_ShrinksOnlyAWallpaperMuchBiggerThanTheScreen(int w, int h, int sw, int sh, int tw, int th) =>
        Assert.Equal((tw, th), EfficientVariant.TargetSize(w, h, (sw, sh)));

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(2304, 1296, 1920, 1080)]   // 1.2x: not worth a re-encode
    [InlineData(3840, 2160, 3840, 2160)]   // a 4K screen keeps 4K
    [InlineData(3840, 2160, 0, 0)]         // screen unknown: never shrink
    public void TargetSize_LeavesTheRestAlone(int w, int h, int sw, int sh) =>
        Assert.Null(EfficientVariant.TargetSize(w, h, (sw, sh)));

    [Fact]
    public void AlreadyImported_MatchesByFingerprint_ThenByNameAndLengthForOlderImports()
    {
        var withPrint = new WallpaperEntry { Id = "1", Title = "x", Category = WallpaperEntry.ImportedCategory, SourceFingerprint = "abc", Duration = 5 };
        var older = new WallpaperEntry { Id = "2", Title = "clip", Category = WallpaperEntry.ImportedCategory, Duration = 10.05 };
        var list = new[] { withPrint, older };

        Assert.Same(withPrint, Importer.AlreadyImported(@"C:\v\other.mp4", "abc", list, 99));
        Assert.Same(older, Importer.AlreadyImported(@"C:\v\clip.mp4", "zzz", list, 10.0));
        Assert.Null(Importer.AlreadyImported(@"C:\v\clip.mp4", "zzz", list, 30.0));    // same name, different length
        Assert.Null(Importer.AlreadyImported(@"C:\v\clip.mp4", null, list, 0));
    }

    [Fact]
    public void Importer_AcceptsCommonVideoExtensionsOnly()
    {
        Assert.True(Importer.IsVideo(@"C:\a\B.MP4"));
        Assert.True(Importer.IsVideo("c.webm"));
        Assert.False(Importer.IsVideo("d.png"));
        Assert.False(Importer.IsVideo("e"));
    }

    // End to end with the real ffmpeg. Skipped (returns early) on a machine without it.
    [Fact]
    public async Task Import_Efficient_Delete_EndToEnd()
    {
        var ffmpeg = Ffmpeg.Locate();
        if (ffmpeg is null) return;
        using var lib = new TempLibrary();

        var source = Path.Combine(lib.Root, "source clip.mp4");
        var (code, err) = await Ffmpeg.RunAsync(ffmpeg,
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=60", "-t", "2",
             "-c:v", "mpeg4", "-pix_fmt", "yuv420p", source], TimeSpan.FromSeconds(60), null, 0, default);
        Assert.True(code == 0, err);

        var reports = new List<double>();
        var imported = await Importer.ImportAsync(ffmpeg, lib.Root, source, progress: new Progress<double>(reports.Add));

        Assert.False(imported.Duplicate);
        var e = imported.Entry;
        Assert.Equal(("source clip", WallpaperEntry.ImportedCategory, 640, 360), (e.Title, e.Category, e.Width, e.Height));
        Assert.InRange(e.Fps, 59, 61);
        Assert.InRange(e.Duration, 1.7, 2.3);
        Assert.True(File.Exists(lib.PathOf(e.File)));
        Assert.True(File.Exists(lib.PathOf(e.Thumbnail)));
        Assert.NotNull(e.SourceFingerprint);
        Assert.Empty(Directory.GetFiles(Path.Combine(lib.Root, "Masters"), ".*"));    // no partial

        // The same file again is recognised, not imported twice.
        var again = await Importer.ImportAsync(ffmpeg, lib.Root, source);
        Assert.True(again.Duplicate);
        Assert.Single(LibraryManifest.Load(lib.Root).Wallpapers);

        // The 30 fps variant.
        Assert.True(await EfficientVariant.EnsureAsync(ffmpeg, lib.Root, e.Id));
        var withVariant = LibraryManifest.Load(lib.Root).Find(e.Id)!;
        Assert.NotNull(withVariant.EfficientFile);
        var variant = await Ffmpeg.ProbeAsync(ffmpeg, lib.PathOf(withVariant.EfficientFile!));
        Assert.InRange(variant!.Fps, 29, 31);

        // Deleting takes the variant, the master and the thumbnail.
        WallpaperDeletion.Delete(lib.Root, new HashSet<string> { e.Id });
        Assert.Empty(LibraryManifest.Load(lib.Root).Wallpapers);
        Assert.Empty(Directory.GetFiles(Path.Combine(lib.Root, "Masters")));
        Assert.Empty(Directory.GetFiles(Path.Combine(lib.Root, "Thumbnails")));
    }
}
