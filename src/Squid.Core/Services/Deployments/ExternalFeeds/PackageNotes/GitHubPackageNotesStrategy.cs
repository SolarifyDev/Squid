using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

public class GitHubPackageNotesStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageNotesStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) =>
        !string.IsNullOrWhiteSpace(feedType) && feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase);

    public async Task<PackageNotesResult> GetNotesAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            return PackageNotesResult.Empty();

        var url = $"https://api.github.com/repos/{packageId}/releases/tags/{version}";
        var headers = BuildHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return PackageNotesResult.Empty();

        if (!response.IsSuccessStatusCode)
            return PackageNotesResult.Failure($"GitHub API returned {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseReleaseNotes(json);
    }

    internal static PackageNotesResult ParseReleaseNotes(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var body = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String ? bodyProp.GetString() : null;
            var name = root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;

            DateTimeOffset? published = null;

            if (root.TryGetProperty("published_at", out var pubProp) && pubProp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(pubProp.GetString(), out var pubDate))
                published = pubDate;

            var notes = !string.IsNullOrWhiteSpace(body) ? body : name;

            if (notes == null && published == null) return PackageNotesResult.Empty();

            return PackageNotesResult.Success(notes, published);
        }
        catch (JsonException)
        {
            return PackageNotesResult.Failure("Failed to parse GitHub release JSON");
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
}
