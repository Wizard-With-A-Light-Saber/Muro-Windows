using Muro.Core.Catalog;

namespace Muro.Core;

/// What the Featured tab shows besides the banner: the newest drop and Muro's Pick.
public static class Featured
{
    /// How many wallpapers Muro's Pick draws.
    public const int PickCount = 12;

    /// The most recent drop: every wallpaper published at the newest publish time, as published.
    /// It stays put until a newer batch lands. A catalog without dates has no drop.
    public static IReadOnlyList<CatalogEntry> LatestDrop(IReadOnlyList<CatalogEntry> catalog)
    {
        var newest = catalog.Max(e => e.PublishedAt);
        return newest is null ? [] : catalog.Where(e => e.PublishedAt == newest).ToList();
    }

    /// Twelve wallpapers at random, drawn once a day, not counting the drop. The draw is kept in
    /// <paramref name="draw"/> and reused for the rest of the day; the caller saves it when it changed.
    /// An empty catalog (not loaded yet) draws nothing and remembers nothing, so the real draw is not
    /// pre-empted by an empty one.
    public static IReadOnlyList<CatalogEntry> Pick(IReadOnlyList<CatalogEntry> catalog, PickDraw draw, DateOnly today, Random random)
    {
        var drop = LatestDrop(catalog).Select(e => e.Id).ToHashSet();
        var pool = new Dictionary<string, CatalogEntry>();
        foreach (var entry in catalog)
            if (!drop.Contains(entry.Id)) pool.TryAdd(entry.Id, entry);
        if (pool.Count == 0) return [];

        var day = today.ToString("yyyy-MM-dd");
        var kept = draw.Ids.Where(pool.ContainsKey).Select(id => pool[id]).ToList();
        if (draw.Day == day && kept.Count >= Math.Min(PickCount, pool.Count)) return kept;

        var drawn = pool.Values.ToArray();
        random.Shuffle(drawn);
        var picked = drawn.Take(PickCount).ToList();
        draw.Ids = picked.Select(e => e.Id).ToList();
        draw.Day = day;
        return picked;
    }
}
