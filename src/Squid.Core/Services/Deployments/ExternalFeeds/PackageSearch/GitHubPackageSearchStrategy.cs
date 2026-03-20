using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

public class GitHubPackageSearchStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageSearchStrategy
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> SearchAsync(ExternalFeed feed, string query, int take, CancellationToken ct)
    {
        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&per_page={take}";
        var headers = BuildHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseResults(json);
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

    private static List<string> ParseResults(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var packages = new List<string>();

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("full_name", out var fullName) && fullName.ValueKind == JsonValueKind.String)
                    packages.Add(fullName.GetString());
            }

            return packages;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
