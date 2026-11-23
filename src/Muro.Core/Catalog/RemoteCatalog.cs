using System.Text.Json;
using Muro.Core.Library;

namespace Muro.Core.Catalog;

/// Why the catalog did not arrive, in the three shapes a person can act on. Explore must never
/// answer a blocked connection with "nothing matches", which blames filters the user cannot fix.
public sealed class CatalogException(CatalogException.FailureKind kind, int status = 0)
    : Exception(Describe(kind, status))
{
    public enum FailureKind
    {
        /// Nothing reached the server: offline, DNS, timeout, VPN or firewall in the way.
        Unreachable,
        /// The server answered with an error status (429 rate limit, 5xx, 404 moved bucket).
        Server,
        /// A 200 that was not the wallpaper list: a captive portal or a filter block page.
        Unreadable,
    }

    public FailureKind Kind { get; } = kind;
    public int Status { get; } = status;

    public string Title => Kind switch
    {
        FailureKind.Unreachable => "Muro cannot reach the internet",
        FailureKind.Server => "The wallpaper catalog is not answering",
        _ => "Something is blocking the wallpaper catalog",
    };

    public string Detail => Kind switch
    {
        FailureKind.Unreachable => "Check that you are online, then try again. A VPN, a firewall or a DNS filter can also stop Muro reaching its wallpapers.",
        FailureKind.Server => $"The server answered with an error ({Status}). This is usually temporary, so try again in a minute.",
        _ => "Muro reached the catalog but what came back was not the wallpaper list. A VPN, a company network or a DNS filter can do this.",
    };

    private static string Describe(FailureKind kind, int status) => kind switch
    {
        FailureKind.Unreachable => "Muro cannot reach the internet.",
        FailureKind.Server => $"The wallpaper catalog is not answering ({status}).",
        _ => "Something is blocking the wallpaper catalog.",
    };
}

public sealed class RemoteCatalog
{
    /// The official catalog.
    public const string DefaultUrl = "https://cdn.murowallpaper.com/catalog.json";

    public List<CatalogEntry> Wallpapers { get; set; } = [];

    /// Always goes to the network. The catalog is served with a short max-age and a stale copy would
    /// hide new wallpapers for minutes.
    public static async Task<RemoteCatalog> FetchAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new() { NoCache = true };
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new CatalogException(CatalogException.FailureKind.Unreachable); }

        using (response)
        {
            // Every error a static host returns arrives as a body. Handing a 27 KB HTML rate-limit
            // page to the decoder made a throttled CDN look like an empty catalog.
            if (!response.IsSuccessStatusCode)
                throw new CatalogException(CatalogException.FailureKind.Server, (int)response.StatusCode);
            try
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                var catalog = JsonSerializer.Deserialize<RemoteCatalog>(body, LibraryManifest.Json)
                              ?? throw new JsonException("null catalog");
                // The interactive wallpapers sit beside the videos; a list that is missing or broken adds nothing.
                if (Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    var listUrl = HostedInteractive.ListUrl(url);
                    foreach (var entry in await HostedInteractive.FetchListAsync(http, listUrl, ct).ConfigureAwait(false))
                        catalog.Wallpapers.Add(HostedInteractive.CatalogEntryFor(entry, listUrl));
                }
                return catalog;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (JsonException) { throw new CatalogException(CatalogException.FailureKind.Unreadable); }
            catch (Exception) { throw new CatalogException(CatalogException.FailureKind.Unreachable); }
        }
    }
}
