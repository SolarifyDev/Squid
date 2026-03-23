using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

public class GitHubPackageVersionStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageVersionStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];

        var url = $"https://api.github.com/repos/{packageId}/releases?per_page={take}";
        var headers = BuildHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseReleases(json);
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
