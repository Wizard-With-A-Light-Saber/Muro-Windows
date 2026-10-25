using System.Net;
using Muro.Core.Catalog;

namespace Muro.Core.Tests;

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}

public class CatalogTests
{
    // The shape the live catalog has today: macOS escaped slashes, ISO dates, optional fields.
    private const string Json = """
        {"wallpapers":[
         {"category":"Nature","duration":30,"fps":30,"height":2304,"id":"c967","preview720":"https:\/\/cdn.example\/p720\/c967.mov",
          "publishedAt":"2026-10-23T03:06:47Z","sizeBytes":77612352,"thumbnail":"https:\/\/cdn.example\/thumbs\/c967.jpg",
          "title":"Misty Mountain Rain","video":"https:\/\/cdn.example\/masters\/c967.mov","width":4096},
         {"category":"Cars","duration":20.5,"fps":60,"height":2160,"id":"e80d","sizeBytes":105188088,
          "thumbnail":"https:\/\/cdn.example\/thumbs\/e80d.jpg","title":"Old Entry","video":"https:\/\/cdn.example\/masters\/e80d.mov","width":3840}]}
        """;

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new FakeHandler(respond));

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Fetch_ParsesTheLiveShape_IncludingMissingOptionalFields()
    {
        var catalog = await RemoteCatalog.FetchAsync(Client(_ => Ok(Json)), "https://x/catalog.json");
        Assert.Equal(2, catalog.Wallpapers.Count);
        var first = catalog.Wallpapers[0];
        Assert.Equal(("c967", "Misty Mountain Rain", 4096, 2304), (first.Id, first.Title, first.Width, first.Height));
        Assert.Equal("https://cdn.example/masters/c967.mov", first.Video);
        Assert.Equal("https://cdn.example/p720/c967.mov", first.Preview720);
        Assert.Equal(new DateTime(2026, 10, 23, 3, 6, 47, DateTimeKind.Utc), first.PublishedAt);
        Assert.Null(catalog.Wallpapers[1].Preview720);
        Assert.Null(catalog.Wallpapers[1].PublishedAt);
    }

    [Fact]
    public async Task Fetch_AsksForAFreshCopy()
    {
        HttpRequestMessage? seen = null;
        await RemoteCatalog.FetchAsync(Client(r => { seen = r; return Ok(Json); }), "https://x/catalog.json");
        Assert.True(seen!.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task Fetch_ASuccessfulPageThatIsNotTheCatalog_IsUnreadable()
    {
        var ex = await Assert.ThrowsAsync<CatalogException>(() =>
            RemoteCatalog.FetchAsync(Client(_ => Ok("<html>Sign in to the hotel wifi</html>")), "https://x/c.json"));
        Assert.Equal(CatalogException.FailureKind.Unreadable, ex.Kind);
    }

    [Fact]
    public async Task Fetch_AnErrorStatus_IsAServerProblem_NotAnEmptyCatalog()
    {
        var ex = await Assert.ThrowsAsync<CatalogException>(() =>
            RemoteCatalog.FetchAsync(Client(_ => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("<html>rate limited</html>") }), "https://x/c.json"));
        Assert.Equal(CatalogException.FailureKind.Server, ex.Kind);
        Assert.Equal(429, ex.Status);
        Assert.Contains("429", ex.Detail);
    }

    [Fact]
    public async Task Fetch_NoConnection_IsUnreachable()
    {
        var ex = await Assert.ThrowsAsync<CatalogException>(() =>
            RemoteCatalog.FetchAsync(Client(_ => throw new HttpRequestException("no route")), "https://x/c.json"));
        Assert.Equal(CatalogException.FailureKind.Unreachable, ex.Kind);
    }

    [Fact]
    public async Task Fetch_WhenTheCallerCancels_ThrowsCancellationNotACatalogError()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RemoteCatalog.FetchAsync(Client(_ => Ok(Json)), "https://x/c.json", cts.Token));
    }

    private static CatalogEntry Entry(string id, DateTime? published) => new() { Id = id, PublishedAt = published };

    [Fact]
    public void Arrivals_FirstRun_SeedsTheSeenSetWithoutBadgingAnything()
    {
        var (news, seen) = CatalogArrivals.Note([Entry("a", DateTime.UtcNow)], null, DateTime.UtcNow);
        Assert.Empty(news);
        Assert.Equal(["a"], seen);
    }

    [Fact]
    public void Arrivals_BadgesOnlyNewAndRecentlyPublishedWallpapers()
    {
        var now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        var catalog = new[]
        {
            Entry("known", now.AddDays(-1)),
            Entry("fresh", now.AddDays(-2)),
            Entry("old", now.AddDays(-90)),      // new to this install but published long ago
            Entry("undated", null),
        };
        var (news, seen) = CatalogArrivals.Note(catalog, ["known"], now);
        Assert.Equal(["fresh"], news);
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void Arrivals_KeepsIdsThatLeftTheCatalog_SoTheyDoNotBadgeIfTheyReturn()
    {
        var (_, seen) = CatalogArrivals.Note([Entry("b", null)], ["a"], DateTime.UtcNow);
        Assert.Equal(["a", "b"], seen.Order());
    }
}
