using System.Net;
using Muro.Core.Catalog;
using Muro.Core.Library;

namespace Muro.Core.Tests;

public class DownloaderTests
{
    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    private static (CatalogEntry Remote, byte[] Video, byte[] Thumb) Sample(int videoBytes = 1_500_000)
    {
        var video = new byte[videoBytes];
        new Random(7).NextBytes(video);
        var remote = new CatalogEntry
        {
            Id = "abc", Title = "Rain", Category = "Nature", Width = 1920, Height = 1080, Fps = 30, Duration = 12,
            SizeBytes = videoBytes, Video = "https://cdn.test/masters/abc.mov", Thumbnail = "https://cdn.test/thumbs/abc.jpg",
        };
        return (remote, video, [1, 2, 3, 4]);
    }

    private static HttpClient Serve(byte[] video, byte[] thumb, HttpStatusCode thumbStatus = HttpStatusCode.OK) =>
        new(new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".mov")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(video) }
            : new HttpResponseMessage(thumbStatus) { Content = new ByteArrayContent(thumb) }));

    [Fact]
    public async Task Download_WritesMasterThumbnailAndManifestRow_AndReportsProgress()
    {
        using var lib = new TempLibrary();
        var (remote, video, thumb) = Sample();
        var reports = new List<double>();

        var entry = await Downloader.DownloadAsync(Serve(video, thumb), remote, lib.Root, new SyncProgress(reports.Add));

        Assert.Equal(video, await File.ReadAllBytesAsync(lib.PathOf("Masters/abc.mov")));
        Assert.Equal(thumb, await File.ReadAllBytesAsync(lib.PathOf("Thumbnails/abc.jpg")));
        var row = Assert.Single(LibraryManifest.Load(lib.Root).Wallpapers);
        Assert.Equal(("abc", "Rain", "Masters/abc.mov", "Thumbnails/abc.jpg", video.Length), (row.Id, row.Title, row.File, row.Thumbnail, (int)row.SizeBytes));
        Assert.Equal(entry.Id, row.Id);
        Assert.True(reports.Count > 2);
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(1.0, reports[^1]);
        Assert.Empty(Directory.GetFiles(Path.Combine(lib.Root, "Masters"), ".*"));   // no partial left behind
    }

    [Fact]
    public async Task Download_ASizeThatDoesNotMatchTheCatalog_FailsAndLeavesNothing()
    {
        using var lib = new TempLibrary();
        var (remote, video, thumb) = Sample();
        remote.SizeBytes = video.Length + 10;

        await Assert.ThrowsAsync<IOException>(() => Downloader.DownloadAsync(Serve(video, thumb), remote, lib.Root));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(lib.Root, "Masters")));
        Assert.Empty(LibraryManifest.Load(lib.Root).Wallpapers);
    }

    [Fact]
    public async Task Download_CancelledPartWay_RemovesThePartialAndAddsNoRow()
    {
        using var lib = new TempLibrary();
        var (remote, video, thumb) = Sample(6_000_000);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Downloader.DownloadAsync(Serve(video, thumb), remote, lib.Root, new SyncProgress(p => { if (p > 0.1) cts.Cancel(); }), cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(lib.Root, "Masters")));
        Assert.Empty(LibraryManifest.Load(lib.Root).Wallpapers);
    }

    [Fact]
    public async Task Download_AMissingThumbnail_DoesNotFailTheDownload()
    {
        using var lib = new TempLibrary();
        var (remote, video, thumb) = Sample();

        var entry = await Downloader.DownloadAsync(Serve(video, thumb, HttpStatusCode.NotFound), remote, lib.Root);

        Assert.Equal("", entry.Thumbnail);
        Assert.True(File.Exists(lib.PathOf("Masters/abc.mov")));
        Assert.Empty(Directory.GetFiles(Path.Combine(lib.Root, "Thumbnails")));
    }

    [Fact]
    public async Task Download_Again_ReplacesTheRowInsteadOfDuplicatingIt()
    {
        using var lib = new TempLibrary();
        var (remote, video, thumb) = Sample();
        await Downloader.DownloadAsync(Serve(video, thumb), remote, lib.Root);
        await Downloader.DownloadAsync(Serve(video, thumb), remote, lib.Root);
        Assert.Single(LibraryManifest.Load(lib.Root).Wallpapers);
    }
}
