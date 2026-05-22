using System.Text.Json;
using System.Xml.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

public class NuGetPackageNotesStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageNotesStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("NuGet", StringComparison.OrdinalIgnoreCase);

    public async Task<PackageNotesResult> GetNotesAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            return PackageNotesResult.Empty();

        if (string.IsNullOrWhiteSpace(feed.FeedUri))
            return PackageNotesResult.Failure("Feed URI is empty");

        var headers = BuildAuthHeaders(feed);

        // Try V3 first, fallback to V2
        var v3Result = await TryGetNotesV3Async(feed.FeedUri, packageId, version, headers, ct).ConfigureAwait(false);

        if (v3Result != null) return v3Result;

        return await GetNotesV2Async(feed.FeedUri, packageId, version, headers, ct).ConfigureAwait(false);
    }

    private async Task<PackageNotesResult> TryGetNotesV3Async(string feedUri, string packageId, string version, Dictionary<string, string> headers, CancellationToken ct)
    {
        var serviceIndexUrl = $"{feedUri.TrimEnd('/')}/index.json";
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        try
        {
            using var indexResponse = await client.GetAsync(serviceIndexUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!indexResponse.IsSuccessStatusCode) return null;

            var indexJson = await indexResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var registrationBase = FindRegistrationBaseUrl(indexJson);

            if (registrationBase == null) return null;

            var leafUrl = $"{registrationBase.TrimEnd('/')}/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}.json";
            var leafClient = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

            using var leafResponse = await leafClient.GetAsync(leafUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!leafResponse.IsSuccessStatusCode) return null;

            var leafJson = await leafResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseNuGetV3RegistrationLeaf(leafJson);
        }
        catch
        {
            return null;
        }
    }

    private async Task<PackageNotesResult> GetNotesV2Async(string feedUri, string packageId, string version, Dictionary<string, string> headers, CancellationToken ct)
    {
        // Use Atom XML — the OData V2 default response format. We do NOT request
        // ?$format=json because the default NuGet.Server (and many derivative
        // private feeds) reject Format as a disallowed query option, returning
        // 400/404 with the OData error:
        //   "URI or query string invalid. Query option 'Format' is not allowed.
        //    To allow it, set the 'AllowedQueryOptions' property on
        //    EnableQueryAttribute or QueryValidationSettings."
        // Asking for Atom XML works on every conformant V2 server (Atom is the
        // default content negotiation) and matches what the sibling V2 strategies
        // (search, version) already do. URL-encode the single quotes inside the
        // OData string literals so feed servers don't reject the raw quotes.
        var encodedId = Uri.EscapeDataString(packageId);
        var encodedVersion = Uri.EscapeDataString(version);
        var url = $"{feedUri.TrimEnd('/')}/Packages(Id=%27{encodedId}%27,Version=%27{encodedVersion}%27)";

        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return PackageNotesResult.Failure($"NuGet feed returned {(int)response.StatusCode}");

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseNuGetV2AtomEntry(xml);
        }
        catch (Exception ex)
        {
            return PackageNotesResult.Failure($"NuGet request failed: {ex.Message}");
        }
    }

    internal static string FindRegistrationBaseUrl(string serviceIndexJson)
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

                if (type != null && type.StartsWith("RegistrationsBaseUrl", StringComparison.Ordinal))
                {
                    if (resource.TryGetProperty("@id", out var idProp))
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

    internal static PackageNotesResult ParseNuGetV3RegistrationLeaf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var entry = root.TryGetProperty("catalogEntry", out var catalogEntry) ? catalogEntry : root;

            var releaseNotes = entry.TryGetProperty("releaseNotes", out var rnProp) && rnProp.ValueKind == JsonValueKind.String ? rnProp.GetString() : null;
            var description = entry.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String ? descProp.GetString() : null;

            DateTimeOffset? published = null;

            if (entry.TryGetProperty("published", out var pubProp) && pubProp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(pubProp.GetString(), out var pubDate))
                published = pubDate;

            var notes = !string.IsNullOrWhiteSpace(releaseNotes) ? releaseNotes : description;

            if (notes == null && published == null) return PackageNotesResult.Empty();

            return PackageNotesResult.Success(notes, published);
        }
        catch (JsonException)
        {
            return PackageNotesResult.Failure("Failed to parse NuGet V3 registration JSON");
        }
    }

    /// <summary>
    /// Parses an OData V2 Atom <c>&lt;entry&gt;</c> response body and extracts
    /// release-notes-relevant fields from <c>&lt;m:properties&gt;</c>:
    /// <c>&lt;d:ReleaseNotes&gt;</c>, <c>&lt;d:Description&gt;</c>,
    /// <c>&lt;d:Published&gt;</c>. Falls back to Description when ReleaseNotes
    /// is blank, matching the V3 catalog leaf parser.
    /// </summary>
    internal static PackageNotesResult ParseNuGetV2AtomEntry(string xml)
    {
        try
        {
            XNamespace d = "http://schemas.microsoft.com/ado/2007/08/dataservices";

            var doc = XDocument.Parse(xml);

            var releaseNotes = doc.Descendants(d + "ReleaseNotes").FirstOrDefault()?.Value;
            var description = doc.Descendants(d + "Description").FirstOrDefault()?.Value;

            DateTimeOffset? published = null;

            var publishedRaw = doc.Descendants(d + "Published").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(publishedRaw) && DateTimeOffset.TryParse(publishedRaw, out var pubDate))
                published = pubDate;

            var notes = !string.IsNullOrWhiteSpace(releaseNotes) ? releaseNotes : description;

            if (string.IsNullOrWhiteSpace(notes) && published == null) return PackageNotesResult.Empty();

            return PackageNotesResult.Success(notes, published);
        }
        catch
        {
            return PackageNotesResult.Failure("Failed to parse NuGet V2 Atom entry");
        }
    }

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed.Username) || string.IsNullOrWhiteSpace(feed.Password))
            return null;

        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{feed.Username}:{feed.Password}"));

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }
}
