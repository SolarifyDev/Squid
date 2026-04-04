using System.Text.Json;
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
        var url = $"{feedUri.TrimEnd('/')}/Packages(Id='{packageId}',Version='{version}')?$format=json";
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return PackageNotesResult.Failure($"NuGet feed returned {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseNuGetV2Entry(json);
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

    internal static PackageNotesResult ParseNuGetV2Entry(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = root.TryGetProperty("d", out var dProp) ? dProp : root;

            var releaseNotes = data.TryGetProperty("ReleaseNotes", out var rnProp) && rnProp.ValueKind == JsonValueKind.String ? rnProp.GetString() : null;
            var description = data.TryGetProperty("Description", out var descProp) && descProp.ValueKind == JsonValueKind.String ? descProp.GetString() : null;

            DateTimeOffset? published = null;

            if (data.TryGetProperty("Published", out var pubProp) && pubProp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(pubProp.GetString(), out var pubDate))
                published = pubDate;

            var notes = !string.IsNullOrWhiteSpace(releaseNotes) ? releaseNotes : description;

            if (notes == null && published == null) return PackageNotesResult.Empty();

            return PackageNotesResult.Success(notes, published);
        }
        catch (JsonException)
        {
            return PackageNotesResult.Failure("Failed to parse NuGet V2 JSON");
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
