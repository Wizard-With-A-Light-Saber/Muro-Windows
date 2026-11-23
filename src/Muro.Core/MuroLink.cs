namespace Muro.Core;

public enum MuroLinkKind { Set, Pause, Resume }

/// A <c>muro://</c> link from a script, a Shortcut or another app (the macOS app has the same links since 6.1):
///   muro://set?id=&lt;wallpaper id&gt;[&amp;display=&lt;monitor id&gt;][&amp;surface=desktop|lockscreen|screensaver|all]
///   muro://pause    muro://resume
/// Any web page can open one, so parsing is strict: an unknown action, surface or an empty id gives null and the
/// link is ignored. What a link may do is limited further by the handler (see the engine's LinkHandler).
public sealed record MuroLink(MuroLinkKind Kind, string? Id = null, string? Display = null, string Surface = "desktop")
{
    public static readonly string[] Surfaces = ["desktop", "lockscreen", "screensaver", "all"];

    public static MuroLink? Parse(string? text)
    {
        if (text is null || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals("muro", StringComparison.OrdinalIgnoreCase)) return null;

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            query[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }

        switch (uri.Host.ToLowerInvariant())
        {
            case "pause": return new MuroLink(MuroLinkKind.Pause);
            case "resume": return new MuroLink(MuroLinkKind.Resume);
            case "set":
                if (!query.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id)) return null;
                var surface = query.TryGetValue("surface", out var s) ? s.ToLowerInvariant() : "desktop";
                if (!Surfaces.Contains(surface)) return null;
                query.TryGetValue("display", out var display);
                return new MuroLink(MuroLinkKind.Set, id.Trim(), string.IsNullOrWhiteSpace(display) ? null : display, surface);
            default: return null;
        }
    }
}
