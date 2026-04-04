using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

public class HelmPackageNotesStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageNotesStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase);

    public async Task<PackageNotesResult> GetNotesAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            return PackageNotesResult.Empty();

        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri))
            return PackageNotesResult.Failure("Invalid feed URI");

        var indexUri = ExternalFeedProbeUri.AppendPath(baseUri, "index.yaml");
        var headers = BuildAuthHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        using var response = await client.GetAsync(indexUri.ToString(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return PackageNotesResult.Failure($"Helm repo returned {(int)response.StatusCode}");

        var yaml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseChartNotes(yaml, packageId, version);
    }

    internal static PackageNotesResult ParseChartNotes(string yaml, string chartName, string version)
    {
        var inEntries = false;
        var inTargetChart = false;
        var inTargetVersion = false;
        var entryPropertyIndent = -1;
        var currentEntryVersion = (string)null;

        string description = null;
        string appVersion = null;
        string created = null;

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

            // Chart name line: "  chartName:"
            if (line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != '-' && !char.IsWhiteSpace(line[2]) && line.EndsWith(":", StringComparison.Ordinal))
            {
                var name = line.TrimStart().TrimEnd(':');
                inTargetChart = string.Equals(name, chartName, StringComparison.OrdinalIgnoreCase);
                entryPropertyIndent = -1;
                inTargetVersion = false;
                continue;
            }

            if (!inTargetChart) continue;

            var trimmed = line.TrimStart();
            var leadingSpaces = line.Length - trimmed.Length;

            // Detect new list entry
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                // If we already found the target version, we're done
                if (inTargetVersion) break;

                if (entryPropertyIndent < 0)
                    entryPropertyIndent = leadingSpaces + 2;

                currentEntryVersion = null;
                description = null;
                appVersion = null;
                created = null;
            }

            // Reject nested properties (deeper indent than entry level)
            if (entryPropertyIndent >= 0 && leadingSpaces > entryPropertyIndent) continue;

            var propLine = trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..] : trimmed;

            if (TryExtractValue(propLine, "version:", out var ver))
            {
                currentEntryVersion = ver;

                if (string.Equals(ver, version, StringComparison.OrdinalIgnoreCase))
                    inTargetVersion = true;
            }
            else if (TryExtractValue(propLine, "description:", out var desc))
            {
                if (inTargetVersion || currentEntryVersion == null)
                    description = desc;
            }
            else if (TryExtractValue(propLine, "appVersion:", out var appVer))
            {
                if (inTargetVersion || currentEntryVersion == null)
                    appVersion = appVer;
            }
            else if (TryExtractValue(propLine, "created:", out var cre))
            {
                if (inTargetVersion || currentEntryVersion == null)
                    created = cre;
            }
        }

        if (!inTargetVersion) return PackageNotesResult.Empty();

        DateTimeOffset? published = null;

        if (!string.IsNullOrEmpty(created) && DateTimeOffset.TryParse(created, out var createdDate))
            published = createdDate;

        var notes = FormatNotes(description, appVersion);

        return PackageNotesResult.Success(notes, published);
    }

    private static string FormatNotes(string description, string appVersion)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(description))
            parts.Add(description);

        if (!string.IsNullOrWhiteSpace(appVersion))
            parts.Add($"App Version: {appVersion}");

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    private static bool TryExtractValue(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[prefix.Length..].Trim().Trim('"').Trim('\'');
            return true;
        }

        value = null;
        return false;
    }

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        if (string.IsNullOrWhiteSpace(feed.Username) || string.IsNullOrWhiteSpace(feed.Password))
            return null;

        var encoded = DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password);

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }
}
