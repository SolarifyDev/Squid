using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

public class HelmPackageVersionStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageVersionStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase);

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];

        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri)) return [];

        var indexUri = ExternalFeedProbeUri.AppendPath(baseUri, "index.yaml");
        var headers = BuildAuthHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        using var response = await client.GetAsync(indexUri.ToString(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return [];

        var yaml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseChartVersions(yaml, packageId, take);
    }

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed.Username) || string.IsNullOrWhiteSpace(feed.Password))
            return null;

        var encoded = DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password);

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }

    /// <summary>
    /// Parses Helm index.yaml to extract versions for a specific chart.
    /// Format:
    ///   entries:
    ///     chartName:
    ///     - version: "1.2.3"
    ///     - version: "1.2.2"
    /// </summary>
    internal static List<string> ParseChartVersions(string yaml, string chartName, int take)
    {
        var versions = new List<string>();
        var inEntries = false;
        var inTargetChart = false;

        using var reader = new StringReader(yaml);

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("entries:", StringComparison.Ordinal))
            {
                inEntries = true;
                continue;
            }

            if (!inEntries) continue;

            // Top-level key outside entries — stop
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                break;

            // Chart name line: "  chartName:"
            if (line.Length > 2 && line[0] == ' ' && line[1] == ' ' && !char.IsWhiteSpace(line[2]) && line.EndsWith(":", StringComparison.Ordinal))
            {
                var name = line.TrimStart().TrimEnd(':');
                inTargetChart = string.Equals(name, chartName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inTargetChart) continue;

            // Version line: "    - version: "1.2.3"" or "      version: 1.2.3"
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("- version:", StringComparison.Ordinal) || trimmed.StartsWith("version:", StringComparison.Ordinal))
            {
                var colonIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
                var versionValue = trimmed[(colonIndex + 1)..].Trim().Trim('"').Trim('\'');

                if (!string.IsNullOrWhiteSpace(versionValue))
                    versions.Add(versionValue);

                if (versions.Count >= take) break;
            }
        }

        return versions;
    }
}
