using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

public class HelmPackageSearchStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageSearchStrategy
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> SearchAsync(ExternalFeed feed, string query, int take, CancellationToken ct)
    {
        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri))
            return [];

        var indexUri = ExternalFeedProbeUri.AppendPath(baseUri, "index.yaml");
        var headers = BuildAuthHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

        using var response = await client.GetAsync(indexUri.ToString(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return [];

        var yaml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseChartNames(yaml, query, take);
    }

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed.Username) || string.IsNullOrWhiteSpace(feed.Password))
            return null;

        var encoded = DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password);

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }

    internal static List<string> ParseChartNames(string yaml, string query, int take)
    {
        var chartNames = new List<string>();
        var inEntries = false;

        using var reader = new StringReader(yaml);

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("entries:", StringComparison.Ordinal))
            {
                inEntries = true;
                continue;
            }

            if (!inEntries) continue;

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                break;

            if (line.Length > 2 && line[0] == ' ' && line[1] == ' ' && !char.IsWhiteSpace(line[2]) && line.EndsWith(":", StringComparison.Ordinal))
            {
                var name = line.TrimStart().TrimEnd(':');

                if (string.IsNullOrWhiteSpace(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    chartNames.Add(name);

                if (chartNames.Count >= take) break;
            }
        }

        return chartNames;
    }
}
