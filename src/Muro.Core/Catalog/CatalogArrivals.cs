namespace Muro.Core.Catalog;

/// Which wallpapers are NEW: those that appeared in the catalog since this install last saw it and
/// were published recently. The first run only seeds the seen set (everything is new to a new user,
/// so badging all of it says nothing), and a long-absent user does not return to a wall of badges.
public static class CatalogArrivals
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public static (HashSet<string> NewIds, HashSet<string> Seen) Note(
        IEnumerable<CatalogEntry> catalog, HashSet<string>? seen, DateTime nowUtc)
    {
        var entries = catalog.ToList();
        var ids = entries.Select(e => e.Id).ToHashSet();
        if (seen is null) return ([], ids);

        var cutoff = nowUtc - Window;
        var recent = entries
            .Where(e => !seen.Contains(e.Id) && e.PublishedAt is { } at && at > cutoff)
            .Select(e => e.Id)
            .ToHashSet();
        var all = new HashSet<string>(seen);
        all.UnionWith(ids);
        return (recent, all);
    }
}
