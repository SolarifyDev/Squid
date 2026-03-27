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
    /// Parses Helm index.yaml to extract chart versions.
    /// Handles both compact format ("- version: X") and standard format
    /// where version is a standalone property at the entry indent level.
    /// Ignores nested version fields (e.g. inside dependencies).
    /// </summary>
    internal static List<string> ParseChartVersions(string yaml, string chartName, int take)
    {
        var versions = new List<string>();
        var inEntries = false;
        var inTargetChart = false;
        var entryPropertyIndent = -1;

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

            // Chart name line: "  chartName:" (not a list item like "  - something:")
            if (line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != '-' && !char.IsWhiteSpace(line[2]) && line.EndsWith(":", StringComparison.Ordinal))
            {
                var name = line.TrimStart().TrimEnd(':');
                inTargetChart = string.Equals(name, chartName, StringComparison.OrdinalIgnoreCase);
                entryPropertyIndent = -1;
                continue;
            }

            if (!inTargetChart) continue;

            var trimmed = line.TrimStart();
            var leadingSpaces = line.Length - trimmed.Length;

            // Detect entry property indent from the first list item
            if (entryPropertyIndent < 0 && trimmed.StartsWith("- ", StringComparison.Ordinal))
                entryPropertyIndent = leadingSpaces + 2;

            if (!trimmed.StartsWith("- version:", StringComparison.Ordinal) && !trimmed.StartsWith("version:", StringComparison.Ordinal))
                continue;

            // Reject nested version fields (e.g. dependency version at deeper indent)
            if (entryPropertyIndent >= 0 && leadingSpaces > entryPropertyIndent)
                continue;

            var colonIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            var versionValue = trimmed[(colonIndex + 1)..].Trim().Trim('"').Trim('\'');

            if (!string.IsNullOrWhiteSpace(versionValue))
                versions.Add(versionValue);

            if (versions.Count >= take) break;
        }

        return versions;
    }
}
