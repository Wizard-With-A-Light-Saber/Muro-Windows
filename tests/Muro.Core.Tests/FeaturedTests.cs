using Muro.Core.Catalog;

namespace Muro.Core.Tests;

public class FeaturedTests
{
    private static CatalogEntry Entry(string id, int? daysAgo) => new()
    {
        Id = id,
        Title = id,
        PublishedAt = daysAgo is { } d ? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-d) : null,
    };

    private static List<CatalogEntry> Catalog(int count, int dropSize = 3) =>
        Enumerable.Range(0, count).Select(i => Entry("w" + i, i < dropSize ? 0 : 10 + i)).ToList();

    [Fact]
    public void LatestDrop_IsEverythingPublishedAtTheNewestTime()
    {
        var drop = Featured.LatestDrop(Catalog(10));
        Assert.Equal(["w0", "w1", "w2"], drop.Select(e => e.Id));
        Assert.Empty(Featured.LatestDrop([]));
        Assert.Empty(Featured.LatestDrop([Entry("a", null), Entry("b", null)]));
    }

    [Fact]
    public void Pick_IsTwelve_NeverTheDrop_AndNoDuplicates()
    {
        var picked = Featured.Pick(Catalog(40), new PickDraw(), new DateOnly(2026, 10, 26), new Random(1));
        Assert.Equal(Featured.PickCount, picked.Count);
        Assert.DoesNotContain(picked, e => e.Id is "w0" or "w1" or "w2");
        Assert.Equal(picked.Count, picked.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Pick_IsStableForTheDay_AndRedrawnTheNext()
    {
        var catalog = Catalog(60);
        var draw = new PickDraw();
        var first = Featured.Pick(catalog, draw, new DateOnly(2026, 10, 26), new Random(1));
        var again = Featured.Pick(catalog, draw, new DateOnly(2026, 10, 26), new Random(2));
        Assert.Equal(first.Select(e => e.Id), again.Select(e => e.Id));
        Assert.Equal("2026-10-26", draw.Day);

        var next = Featured.Pick(catalog, draw, new DateOnly(2026, 10, 27), new Random(2));
        Assert.NotEqual(first.Select(e => e.Id), next.Select(e => e.Id));
        Assert.Equal("2026-10-27", draw.Day);
    }

    [Fact]
    public void Pick_FromAnEmptyCatalog_DrawsNothingAndRemembersNothing()
    {
        var draw = new PickDraw();
        Assert.Empty(Featured.Pick([], draw, new DateOnly(2026, 10, 26), new Random(1)));
        Assert.Null(draw.Day);
        // The real catalog arriving later still gets its draw that day.
        Assert.Equal(12, Featured.Pick(Catalog(30), draw, new DateOnly(2026, 10, 26), new Random(1)).Count);
    }

    [Fact]
    public void Pick_DropsWallpapersTheCatalogNoLongerHas_AndTopsUp()
    {
        var catalog = Catalog(40);
        var draw = new PickDraw();
        var first = Featured.Pick(catalog, draw, new DateOnly(2026, 10, 26), new Random(1));
        var gone = first[0].Id;
        var trimmed = catalog.Where(e => e.Id != gone).ToList();
        var after = Featured.Pick(trimmed, draw, new DateOnly(2026, 10, 26), new Random(3));
        Assert.Equal(12, after.Count);
        Assert.DoesNotContain(after, e => e.Id == gone);
    }

    [Fact]
    public void Pick_FromASmallCatalog_ReturnsWhatThereIs()
    {
        var picked = Featured.Pick(Catalog(6), new PickDraw(), new DateOnly(2026, 10, 26), new Random(1));
        Assert.Equal(3, picked.Count);   // six, minus a drop of three
    }

    [Fact]
    public void Settings_RoundTrip_AndDefaultsSurviveAMissingOrBrokenFile()
    {
        using var lib = new TempLibrary();
        Assert.Equal("smooth", AppSettings.Load(lib.Root).DefaultMode);
        AppSettings.Update(lib.Root, s => { s.DefaultMode = "efficient"; s.HeroId = "abc"; s.Pick = new PickDraw { Ids = ["x"], Day = "2026-10-26" }; });
        var back = AppSettings.Load(lib.Root);
        Assert.Equal((true, "abc", "2026-10-26"), (back.Efficient, back.HeroId, back.Pick.Day));
        File.WriteAllText(AppSettings.PathIn(lib.Root), "{ not json");
        Assert.Equal("smooth", AppSettings.Load(lib.Root).DefaultMode);
    }

    [Fact]
    public void EngineConfig_Update_KeepsEverythingElse_AndTheTrayIsOnByDefault()
    {
        using var lib = new TempLibrary();
        Assert.True(EngineConfig.Load(lib.Root).ShowTrayIcon);
        EngineConfig.Update(lib.Root, c => c.AllDisplays = new EngineConfig.Assignment("w1"));
        EngineConfig.Update(lib.Root, c => c.ShowTrayIcon = false);
        var back = EngineConfig.Load(lib.Root);
        Assert.Equal(("w1", false), (back.AllDisplays!.WallpaperID, back.ShowTrayIcon));
    }
}
