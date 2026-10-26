using System.Text.Json;

namespace Muro.Core.Updates;

/// One group of release-note lines. <see cref="Name"/> is null for notes written without headings.
public sealed record ReleaseNoteSection(string? Name, IReadOnlyList<string> Lines);

/// A release as GitHub describes it: what the app needs to say a new Muro exists, show what changed
/// and hand over the download. Release notes are free text written months from now, so a shape we
/// do not recognise means "no update to show", never an error in the user's face.
public sealed record ReleaseInfo(
    string Version,
    string? Title,
    Uri Page,
    Uri? DownloadUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseNoteSection> Notes)
{
    public bool IsEmpty => Notes.All(n => n.Lines.Count == 0);

    /// The Windows installer among a release's assets. The macOS release ships a .dmg and this
    /// repository also carries wallpaper-storage releases, so only an .exe or .msi, or a .zip whose
    /// name says win, counts. The installer is preferred over the zip.
    public static bool IsWindowsAsset(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".exe") || n.EndsWith(".msi") || (n.EndsWith(".zip") && n.Contains("win"));
    }

    public static ReleaseInfo? From(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        if (Str(json, "tag_name") is not { } tag || Str(json, "html_url") is not { } pageText) return null;
        if (!Uri.TryCreate(pageText, UriKind.Absolute, out var page)) return null;

        var version = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (version.Length == 0) return null;

        Uri? download = null;
        if (json.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var windows = assets.EnumerateArray()
                .Select(a => (Name: Str(a, "name"), Url: Str(a, "browser_download_url")))
                .Where(a => a.Name is not null && a.Url is not null && IsWindowsAsset(a.Name))
                // The installer first, then any other exe (the single-file Muro-Portable.exe), then the zip.
                .OrderBy(a => a.Name!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? 2 : a.Name!.Contains("setup", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (windows.Url is not null && Uri.TryCreate(windows.Url, UriKind.Absolute, out var u)) download = u;
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(Str(json, "published_at"), out var at) ? at : null;

        // A release name that only repeats the version the card already shows ("v3.0", "Muro 3.0") is noise.
        var title = Str(json, "name")?.Trim();
        if (title is not null && (title.Length == 0 || new[] { tag, version, $"muro {version}", $"muro {tag}" }
                .Contains(title, StringComparer.OrdinalIgnoreCase)))
            title = null;

        return new ReleaseInfo(version, title, page, download, published, ReleaseNotes.Parse(Str(json, "body") ?? ""));
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// Turns a GitHub release body into something the What's New dialog can lay out. A deliberately
/// small subset of Markdown: headings and bullets. Anything cleverer is not worth a parser.
public static class ReleaseNotes
{
    /// A release body has no size limit and this is drawn on the UI thread.
    public const int LineLimit = 60;

    public static IReadOnlyList<ReleaseNoteSection> Parse(string markdown)
    {
        var sections = new List<ReleaseNoteSection>();
        string? name = null;
        var lines = new List<string>();
        var total = 0;

        void Flush()
        {
            if (lines.Count > 0) sections.Add(new ReleaseNoteSection(name, lines.ToArray()));
            lines.Clear();
        }

        foreach (var raw in markdown.Split(['\r', '\n']))
        {
            if (total >= LineLimit) break;
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                Flush();
                name = Clean(line.TrimStart('#')).Trim(':', ' ');
                if (name.Length == 0) name = null;
                continue;
            }

            // Rules, images, HTML and the auto-appended "Full Changelog" footer are chrome, not notes.
            if (line.StartsWith("---") || line.StartsWith("***")) continue;
            if (line.StartsWith('!') || line.StartsWith('<')) continue;
            if (line.Contains("full changelog", StringComparison.OrdinalIgnoreCase)) continue;

            var text = line;
            foreach (var marker in new[] { "- ", "* ", "+ " })
                if (text.StartsWith(marker)) { text = text[marker.Length..]; break; }

            // Numbered bullets: "1. Something". Needs the space, so "1.5 million" is left alone.
            var dot = text.IndexOf('.');
            if (dot is >= 1 and <= 2 && text[..dot].All(char.IsDigit) && dot + 1 < text.Length && text[dot + 1] == ' ')
                text = text[(dot + 1)..].Trim();

            var cleaned = Clean(text);
            if (cleaned.Length == 0) continue;
            lines.Add(cleaned);
            total++;
        }
        Flush();
        return sections;
    }

    /// Strips the emphasis markers, link syntax and stray backticks that would otherwise be shown literally.
    private static string Clean(string text)
    {
        var s = text;
        while (true)
        {
            var open = s.IndexOf('[');
            if (open < 0) break;
            var close = s.IndexOf(']', open);
            if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(') break;
            var end = s.IndexOf(')', close);
            if (end < 0) break;
            s = s[..open] + s[(open + 1)..close] + s[(end + 1)..];
        }
        return s.Replace("**", "").Replace("`", "").Replace("__", "").Trim();
    }
}
