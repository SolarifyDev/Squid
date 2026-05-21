using System.Text.Json;
using System.Xml.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

/// <summary>
/// Enumerates every version of a NuGet package known to the feed. Tries V3
/// <c>PackageBaseAddress</c> first (the flat container that serves
/// <c>{base}/{id-lower}/index.json</c> with a <c>versions[]</c> array); on
/// failure falls back to V2 OData <c>/FindPackagesById()</c> with inline
/// next-link pagination.
///
/// <para><b>Critical contract note (per <see cref="IPackageVersionStrategy"/>)</b>:
/// this method returns ALL upstream versions up to
/// <see cref="PackageVersionEnumerationCap"/>. The caller —
/// <c>ExternalFeedPackageVersionService</c> — applies
/// <see cref="PackageVersionFilter.Apply"/> to do filter + semver sort + take.
/// Don't pre-truncate here: a freshly pushed <c>1.1.0</c> can appear AFTER
/// many lex-late <c>1.0.x-N</c> entries on a server that orders by upload
/// time, and pre-truncation would hide it.</para>
///
/// <para><b>Package ID casing</b>: NuGet V3's flat container is case-sensitive
/// (the path segment is always lowercase). NuGet package IDs are documented
/// case-insensitive, so we lowercase before the V3 path lookup. V2 is
/// case-insensitive in queries; we pass the operator's casing verbatim.</para>
///
/// <para><b>Coverage note</b>: closes the Phase-3 gap where IIS-deploy
/// operators selecting a NuGet feed couldn't list versions even when they
/// typed the exact package ID manually (the version dropdown stayed empty).
/// Sibling <c>NuGetPackageSearchStrategy</c> closes the search half.</para>
/// </summary>
public class NuGetPackageVersionStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageVersionStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>V3 service-index resource <c>@type</c> prefix for the flat-container endpoint.</summary>
    internal const string PackageBaseAddressTypePrefix = "PackageBaseAddress";

    private const string DataServicesNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("NuGet", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];
        if (string.IsNullOrWhiteSpace(feed.FeedUri)) return [];

        var headers = BuildAuthHeaders(feed);
        var enumerationCap = PackageVersionEnumerationCap.Resolve();

        var v3 = await TryListVersionsV3Async(feed.FeedUri, packageId, headers, enumerationCap, ct).ConfigureAwait(false);

        if (v3 != null) return v3;

        return await ListVersionsV2Async(feed.FeedUri, packageId, headers, enumerationCap, ct).ConfigureAwait(false);
    }

    // ── V3 path ────────────────────────────────────────────────────────────────

    private async Task<List<string>> TryListVersionsV3Async(string feedUri, string packageId, Dictionary<string, string> headers, int enumerationCap, CancellationToken ct)
    {
        try
        {
            var serviceIndexUrl = $"{feedUri.TrimEnd('/')}/index.json";
            var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

            using var indexResponse = await client.GetAsync(serviceIndexUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!indexResponse.IsSuccessStatusCode) return null;

            var indexJson = await indexResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var packageBaseUrl = FindPackageBaseAddressUrl(indexJson);

            if (packageBaseUrl == null) return null;

            var versionsUrl = $"{packageBaseUrl.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json";
            var versionsClient = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

            using var versionsResponse = await versionsClient.GetAsync(versionsUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!versionsResponse.IsSuccessStatusCode) return null;

            var versionsJson = await versionsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseV3VersionsResponse(versionsJson, enumerationCap);
        }
        catch
        {
            return null;
        }
    }

    internal static string FindPackageBaseAddressUrl(string serviceIndexJson)
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

                if (type != null && type.StartsWith(PackageBaseAddressTypePrefix, StringComparison.Ordinal))
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

    internal static List<string> ParseV3VersionsResponse(string json, int enumerationCap)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<string>();

            foreach (var item in versions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var v = item.GetString();

                if (string.IsNullOrWhiteSpace(v)) continue;

                result.Add(v);

                if (result.Count >= enumerationCap) break;
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ── V2 path (with inline OData <link rel="next"> pagination) ─────────────

    private async Task<List<string>> ListVersionsV2Async(string feedUri, string packageId, Dictionary<string, string> headers, int enumerationCap, CancellationToken ct)
    {
        try
        {
            var url = BuildV2VersionsUrl(feedUri, packageId);
            var versions = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pageBudget = 50;    // belt-and-braces: cap on page count even if cap not reached

            while (!string.IsNullOrWhiteSpace(url) && versions.Count < enumerationCap && pageBudget-- > 0)
            {
                var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) break;

                var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var (pageVersions, nextLink) = ParseV2VersionsResponse(xml);

                foreach (var v in pageVersions)
                {
                    if (!seen.Add(v)) continue;
                    versions.Add(v);

                    if (versions.Count >= enumerationCap) break;
                }

                url = nextLink;
            }

            return versions;
        }
        catch
        {
            return [];
        }
    }

    internal static string BuildV2VersionsUrl(string feedUri, string packageId)
    {
        var escaped = (packageId ?? string.Empty).Replace("'", "''");
        var encodedId = Uri.EscapeDataString($"'{escaped}'");

        // semVerLevel=2.0.0 ensures SemVer 2.0 versions (e.g. 1.0.0-beta.1+build.2)
        // aren't silently dropped by servers honouring the extension. Older
        // NuGet.Server installations ignore unknown params, so this is safe to send
        // unconditionally.
        return $"{feedUri.TrimEnd('/')}/FindPackagesById()?id={encodedId}&semVerLevel=2.0.0";
    }

    /// <summary>
    /// Parses one page of an OData Atom feed response. Returns the versions
    /// extracted from <c>&lt;entry&gt;&lt;m:properties&gt;&lt;d:Version&gt;</c>
    /// (prefers <c>&lt;d:NormalizedVersion&gt;</c> when present) and the
    /// <c>&lt;link rel="next"&gt;</c> URL for the next page, or null when
    /// the page has no continuation.
    /// </summary>
    internal static (List<string> versions, string nextLink) ParseV2VersionsResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace d = DataServicesNamespace;
            XNamespace atom = AtomNamespace;

            var versions = new List<string>();

            foreach (var entry in doc.Descendants(atom + "entry"))
            {
                var properties = entry.Descendants(d + "NormalizedVersion").FirstOrDefault()
                                ?? entry.Descendants(d + "Version").FirstOrDefault();

                var version = properties?.Value?.Trim();

                if (!string.IsNullOrWhiteSpace(version)) versions.Add(version);
            }

            var nextLink = doc.Descendants(atom + "link")
                .FirstOrDefault(link =>
                {
                    var rel = link.Attribute("rel")?.Value;
                    return string.Equals(rel, "next", StringComparison.OrdinalIgnoreCase);
                })?.Attribute("href")?.Value;

            return (versions, nextLink);
        }
        catch
        {
            return (new List<string>(), null);
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
