using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

public class GitHubPackageVersionStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageVersionStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// GitHub Releases API per-page maximum. Asking for the largest page minimises
    /// round-trips for typical feeds; pagination via RFC 5988 Link header
    /// (<c>rel="next"</c>) covers feeds that exceed 100 releases.
    /// </summary>
    internal const int MaxReleasesPerPage = 100;

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];

        var firstPageUrl = new Uri($"https://api.github.com/repos/{packageId}/releases?per_page={MaxReleasesPerPage}");
        var headers = BuildHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        return await PaginateReleasesAsync(client, firstPageUrl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Follow GitHub's RFC 5988 Link header until exhaustion or the enumeration
    /// sanity cap. GitHub returns releases newest-first; pagination preserves
    /// that order so a fresh release at position 1 surfaces even on huge repos.
    /// </summary>
    private static async Task<List<string>> PaginateReleasesAsync(HttpClient client, Uri firstPageUrl, CancellationToken ct)
    {
        var cap = PackageVersionEnumerationCap.Resolve();
        var accumulated = new List<string>();
        var currentUri = firstPageUrl;

        while (currentUri != null && accumulated.Count < cap)
        {
            using var response = await client.GetAsync(currentUri.ToString(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return accumulated;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            AppendReleases(accumulated, ParseReleases(json), cap);

            if (accumulated.Count >= cap) return accumulated;

            if (!LinkHeaderParser.TryGetNextUri(response, currentUri, out var nextUri))
                return accumulated;

            currentUri = nextUri;
        }

        return accumulated;
    }

    private static void AppendReleases(List<string> accumulated, List<string> page, int cap)
    {
        foreach (var release in page)
        {
            if (accumulated.Count >= cap) return;

            accumulated.Add(release);
        }
    }

    private static Dictionary<string, string> BuildHeaders(ExternalFeed feed)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "Squid",
            ["Accept"] = "application/vnd.github.v3+json"
        };

        if (!string.IsNullOrWhiteSpace(feed.Password))
            headers["Authorization"] = $"token {feed.Password}";

        return headers;
    }

    /// <summary>
    /// Pure parser for one releases-page response body. Order preserved as
    /// returned by GitHub (newest first). NO truncation — pagination + take
    /// happen in <see cref="PaginateReleasesAsync"/> + <see cref="PackageVersionFilter.Apply"/>.
    /// </summary>
    internal static List<string> ParseReleases(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array) return [];

            var versions = new List<string>();

            foreach (var item in root.EnumerateArray())
            {
                if (item.TryGetProperty("tag_name", out var tagName) && tagName.ValueKind == JsonValueKind.String)
                    versions.Add(tagName.GetString());
            }

            return versions;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
