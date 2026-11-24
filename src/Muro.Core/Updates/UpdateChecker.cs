using System.Text.Json;

namespace Muro.Core.Updates;

/// Looks for a newer Windows build among the GitHub releases.
///
/// Not `/releases/latest`: that returns the newest release of any platform, and the macOS release
/// (a .dmg, tagged 5.x) would hide every Windows release behind it, or be reported as an update
/// forever. The newest non-draft, non-prerelease release that carries a Windows asset and is newer
/// than this build wins; no such release means up to date.
public static class UpdateChecker
{
    public const string ReleasesUrl = "https://api.github.com/repos/Wizard-With-A-Light-Saber/Muro-Windows/releases?per_page=20";

    /// Throws on network or HTTP failure so a user-initiated check can say it failed; the automatic
    /// check swallows that. Returns null when this build is current.
    public static async Task<ReleaseInfo?> FindAsync(HttpClient http, string current, string url = ReleasesUrl, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Pick(doc.RootElement, current);
    }

    public static ReleaseInfo? Pick(JsonElement releases, string current)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        ReleaseInfo? best = null;
        foreach (var json in releases.EnumerateArray())
        {
            if (Flag(json, "draft") || Flag(json, "prerelease")) continue;
            if (ReleaseInfo.From(json) is not { DownloadUrl: not null } release) continue;
            if (!AppVersion.IsNewer(release.Version, current)) continue;
            if (best is null || AppVersion.IsNewer(release.Version, best.Version)) best = release;
        }
        return best;
    }

    private static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
