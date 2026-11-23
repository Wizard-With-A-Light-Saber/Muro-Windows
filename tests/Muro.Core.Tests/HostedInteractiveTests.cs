using System.Security.Cryptography;
using System.Text;
using Muro.Core.Catalog;
using Muro.Core.Library;

namespace Muro.Core.Tests;

public class HostedInteractiveTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HostedInteractive.Entry Make(string id, Dictionary<string, byte[]> files) => new()
    {
        Id = id, Title = "Test", Added = "2026-11-13", Version = "abc123",
        Files = files.Select(f => new HostedInteractive.File { Path = f.Key, Size = f.Value.Length, Sha256 = Sha(f.Value) }).ToList(),
    };

    private static Dictionary<string, byte[]> Good() => new()
    {
        ["index.html"] = Encoding.UTF8.GetBytes("<html></html>"),
        ["still.jpg"] = [1, 2, 3],
        ["thumb.jpg"] = [4, 5, 6],
    };

    private sealed class Server(Dictionary<string, byte[]> served) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.Segments[^1];
            return Task.FromResult(served.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "muro-hosted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData("index.html", true)]
    [InlineData("img/still.jpg", true)]
    [InlineData("../evil.html", false)]
    [InlineData("a/../b.html", false)]
    [InlineData(".hidden", false)]
    [InlineData("UPPER.html", false)]
    [InlineData("C:/x.html", false)]
    [InlineData("a//b.html", false)]
    [InlineData("", false)]
    public void Paths_StayInsideTheWallpaperFolder(string path, bool safe) => Assert.Equal(safe, HostedInteractive.IsSafePath(path));

    [Fact]
    public void AnEntryNeedsItsThreeFiles_SafeNames_AndRealHashes()
    {
        Assert.True(HostedInteractive.IsUsable(Make("interactive-a", Good())));

        var missing = Good(); missing.Remove("still.jpg");
        Assert.False(HostedInteractive.IsUsable(Make("interactive-a", missing)));
        Assert.False(HostedInteractive.IsUsable(Make("../a", Good())));

        var badHash = Make("interactive-a", Good());
        badHash.Files[0].Sha256 = "nothex";
        Assert.False(HostedInteractive.IsUsable(badHash));
    }

    [Fact]
    public void ParseList_DropsOneBrokenEntry_NotTheWholeList_AndSortsByOrder()
    {
        var a = Make("interactive-b", Good()); a.Order = 2;
        var b = Make("interactive-a", Good()); b.Order = 1;
        string Json(HostedInteractive.Entry e) => System.Text.Json.JsonSerializer.Serialize(e, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var json = "{\"wallpapers\":[" + Json(a) + ",{\"id\":\"BAD ID\"}," + Json(b) + ",42]}";

        var list = HostedInteractive.ParseList(json);

        Assert.Equal(["interactive-a", "interactive-b"], list.Select(e => e.Id));
    }

    [Fact]
    public async Task Download_ChecksEveryFile_AndAddsALibraryEntryThatPlaysTheStill()
    {
        var root = TempRoot();
        try
        {
            var files = Good();
            var remote = HostedInteractive.CatalogEntryFor(Make("interactive-a", files), "https://example.test/interactive.json");
            using var http = new HttpClient(new Server(files));

            var entry = await Downloader.DownloadAsync(http, remote, root);

            Assert.True(entry.IsInteractive);
            Assert.Equal("Hosted Interactive/interactive-a/still.jpg", entry.File);
            Assert.True(File.Exists(Path.Combine(HostedInteractive.FolderFor(root, "interactive-a"), "index.html")));
            Assert.NotNull(LibraryManifest.Load(root).Find("interactive-a"));
            var resolved = LibraryResolver.Resolve(root, new EngineConfig.Assignment("interactive-a"));
            Assert.EndsWith("still.jpg", resolved!.Value.Path);
            Assert.EndsWith("index.html", resolved.Value.InteractivePage);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_RejectsAFileThatDoesNotMatch_AndLeavesNothingBehind()
    {
        var root = TempRoot();
        try
        {
            var entry = Make("interactive-a", Good());
            var served = Good();
            served["index.html"] = new byte[entry.Files[0].Size];                  // same size, different bytes
            var remote = HostedInteractive.CatalogEntryFor(entry, "https://example.test/interactive.json");
            using var http = new HttpClient(new Server(served));

            await Assert.ThrowsAsync<IOException>(() => Downloader.DownloadAsync(http, remote, root));

            Assert.False(Directory.Exists(HostedInteractive.FolderFor(root, "interactive-a")));
            Assert.Null(LibraryManifest.Load(root).Find("interactive-a"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, HostedInteractive.FolderName)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Deleting_AnInteractiveWallpaper_RemovesItsWholeFolder()
    {
        var root = TempRoot();
        try
        {
            var files = Good();
            var remote = HostedInteractive.CatalogEntryFor(Make("interactive-a", files), "https://example.test/interactive.json");
            using var http = new HttpClient(new Server(files));
            await Downloader.DownloadAsync(http, remote, root);

            WallpaperDeletion.Delete(root, new HashSet<string> { "interactive-a" });

            Assert.False(Directory.Exists(HostedInteractive.FolderFor(root, "interactive-a")));
        }
        finally { Directory.Delete(root, true); }
    }
}
