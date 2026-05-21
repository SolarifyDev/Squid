using System.Text.Json;
using System.Xml.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

/// <summary>
/// Searches package IDs on a NuGet feed by query prefix. Tries V3
/// <c>SearchQueryService</c> first (via the service index at
/// <c>{feedUri}/index.json</c>); if that fails (404 / not a V3 feed / no
/// <c>SearchQueryService</c> resource declared), falls back to V2 OData
/// <c>/Search()</c>. The two-protocol dance mirrors
/// <see cref="PackageNotes.NuGetPackageNotesStrategy"/>'s pattern — every
/// NuGet feed type Squid supports speaks at least one of the two protocols.
///
/// <para><b>Why V3 first</b>: V3 is the modern protocol every public NuGet
/// feed (NuGet.org, GitHub Packages NuGet, Azure Artifacts) and most modern
/// private servers (BaGet, Sleet, ProGet) speak. V2 is the older OData
/// protocol that older private NuGet.Server installations still serve
/// exclusively (e.g. on-prem servers that haven't been upgraded).</para>
///
/// <para><b>Why prerelease + semVerLevel matter</b>: NuGet V3
/// <c>SearchQueryService</c> defaults to <c>prerelease=false</c> +
/// <c>semVerLevel</c> unset. A feed of only-prerelease packages (1.0.0-rc1)
/// or SemVer 2.0 packages (1.0.0-beta.1+build.2) would silently return zero
/// hits. We always send <c>prerelease=true</c> + <c>semVerLevel=2.0.0</c> so
/// the operator sees every package the feed actually publishes.</para>
///
/// <para><b>Coverage note</b>: this was the Phase-3 missing piece that
/// caused IIS deploy operators to see "no results" when picking ANY NuGet
/// feed. Sibling sites <c>NuGetPackageVersionStrategy</c> closes the
/// "selected the package, can't pick a version" half of the same bug.</para>
/// </summary>
public class NuGetPackageSearchStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageSearchStrategy
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>NuGet V3 service-index resource <c>@type</c> prefix for the search endpoint.</summary>
    internal const string SearchQueryServiceTypePrefix = "SearchQueryService";

    /// <summary>OData namespace prefix used in V2 NuGet responses.</summary>
    private const string DataServicesNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices";

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("NuGet", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> SearchAsync(ExternalFeed feed, string query, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(feed.FeedUri)) return [];
        if (take <= 0) take = 20;

        var headers = BuildAuthHeaders(feed);

        var v3 = await TrySearchV3Async(feed.FeedUri, query, take, headers, ct).ConfigureAwait(false);

        if (v3 != null) return v3;

        return await SearchV2Async(feed.FeedUri, query, take, headers, ct).ConfigureAwait(false);
    }

    // ── V3 path ────────────────────────────────────────────────────────────────

    private async Task<List<string>> TrySearchV3Async(string feedUri, string query, int take, Dictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            var serviceIndexUrl = $"{feedUri.TrimEnd('/')}/index.json";
            var client = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

            using var indexResponse = await client.GetAsync(serviceIndexUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!indexResponse.IsSuccessStatusCode) return null;

            var indexJson = await indexResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var searchUrl = FindSearchQueryServiceUrl(indexJson);

            if (searchUrl == null) return null;

            var searchEndpoint = BuildV3SearchEndpoint(searchUrl, query, take);
            var searchClient = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

            using var searchResponse = await searchClient.GetAsync(searchEndpoint, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!searchResponse.IsSuccessStatusCode) return null;

            var searchJson = await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseV3SearchResponse(searchJson);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the V3 service-index <c>resources</c> array looking for the first
    /// resource whose <c>@type</c> starts with <c>SearchQueryService</c>
    /// (versions <c>3.0.0-beta</c> / <c>3.0.0-rc</c> / <c>3.0.0</c> / <c>3.5.0</c>
    /// all share that prefix). Returns the resource's <c>@id</c>, or null when
    /// the feed isn't V3 / has no search service declared.
    /// </summary>
    internal static string FindSearchQueryServiceUrl(string serviceIndexJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(serviceIndexJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var resource in resources.EnumerateArray())
            {
                if (!resource.TryGetProperty("@type", out var typeProp)) continue;

                var type = typeProp.GetString();

                if (type != null && type.StartsWith(SearchQueryServiceTypePrefix, StringComparison.Ordinal))
                {
                    if (resource.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        return idProp.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string BuildV3SearchEndpoint(string searchServiceUrl, string query, int take)
    {
        var encodedQuery = Uri.EscapeDataString(query ?? string.Empty);
        var separator = searchServiceUrl.Contains('?') ? "&" : "?";

        // prerelease=true + semVerLevel=2.0.0 are non-negotiable: see class doc-comment.
        return $"{searchServiceUrl}{separator}q={encodedQuery}&take={take}&prerelease=true&semVerLevel=2.0.0";
    }

    internal static List<string> ParseV3SearchResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var packages = new List<string>();

            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) packages.Add(id);
                }
            }

            return packages;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ── V2 path ────────────────────────────────────────────────────────────────

    private async Task<List<string>> SearchV2Async(string feedUri, string query, int take, Dictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            var url = BuildV2SearchUrl(feedUri, query, take);
            var client = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return [];

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseV2SearchResponse(xml, take);
        }
        catch
        {
            return [];
        }
    }

    internal static string BuildV2SearchUrl(string feedUri, string query, int take)
    {
        // OData string-literal escape: single quotes inside the string double up.
        var escaped = (query ?? string.Empty).Replace("'", "''");
        var encodedQuery = Uri.EscapeDataString($"'{escaped}'");

        // includePrerelease=true matches V3's prerelease=true. semVerLevel=2.0.0 is
        // a NuGet V2 extension some servers honour (NuGet.Server >= 3.4); harmless
        // on older servers that ignore it.
        return $"{feedUri.TrimEnd('/')}/Search()?$top={take}&searchTerm={encodedQuery}&includePrerelease=true&semVerLevel=2.0.0";
    }

    /// <summary>
    /// Parses an OData Atom feed XML and extracts unique package IDs from
    /// <c>&lt;entry&gt;&lt;m:properties&gt;&lt;d:Id&gt;</c>. Dedupes because V2
    /// <c>/Search()</c> can return multiple entries per package (one per version)
    /// when <c>$filter</c> isn't constraining to <c>IsLatestVersion</c>.
    /// </summary>
    internal static List<string> ParseV2SearchResponse(string xml, int take)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace d = DataServicesNamespace;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packages = new List<string>();

            foreach (var idElement in doc.Descendants(d + "Id"))
            {
                var id = idElement.Value?.Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!seen.Add(id)) continue;

                packages.Add(id);

                if (packages.Count >= take) break;
            }

            return packages;
        }
        catch
        {
            return [];
        }
    }

    // ── Auth ───────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed.Username) || string.IsNullOrWhiteSpace(feed.Password))
            return null;

        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{feed.Username}:{feed.Password}"));

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }
}
