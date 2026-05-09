using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

public class DockerPackageVersionStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageVersionStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal static readonly Uri DockerHubRegistryBaseUri = new("https://registry-1.docker.io/v2/");

    /// <summary>
    /// Tags-per-page hint asked of the registry. The reference distribution
    /// (and most clones — Harbor, ECR, GHCR) honour <c>?n=N</c> up to ~100; some
    /// implementations cap silently below that. We then follow the
    /// <c>Link: &lt;url&gt;; rel="next"</c> header until exhaustion or the
    /// enumeration sanity cap is hit. Asking for the largest reasonable page
    /// minimises round-trips for typical feeds.
    /// </summary>
    internal const int MaxTagsPerPage = 100;

    public bool CanHandle(string feedType) => DockerRegistryAuthHelper.IsContainerRegistryFeed(new ExternalFeed { FeedType = feedType });

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];

        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri)) return [];

        var v2Base = ResolveRegistryV2Base(baseUri);
        var repo = IsDockerHub(baseUri) && !packageId.Contains('/') ? $"library/{packageId}" : packageId;

        return await ListRegistryTagsAsync(feed, v2Base, repo, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Page the registry's tags/list endpoint via RFC 5988 <c>rel="next"</c>
    /// links, accumulating into a single list. On a 401 challenge for the first
    /// page we upgrade to a bearer token (for Docker Hub / GHCR / ECR-style
    /// servers) and retry the same URL; the bearer is then reused for every
    /// subsequent page. Stops at <see cref="PackageVersionEnumerationCap"/>.
    /// </summary>
    private async Task<List<string>> ListRegistryTagsAsync(ExternalFeed feed, Uri v2Base, string repo, CancellationToken ct)
    {
        var firstPageUrl = new Uri($"{v2Base.ToString().TrimEnd('/')}/{repo}/tags/list?n={MaxTagsPerPage}");
        var cap = PackageVersionEnumerationCap.Resolve();

        var accumulated = new List<string>();
        var currentUri = firstPageUrl;
        string bearerToken = null;

        while (currentUri != null && accumulated.Count < cap)
        {
            var headers = BuildAuthHeaders(feed, bearerToken);
            var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

            using var response = await client.GetAsync(currentUri.ToString(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized && bearerToken == null)
            {
                // Only the first-page 401 triggers a bearer upgrade. Mid-pagination
                // 401s are treated as terminal — we surface what we have rather
                // than silently looping while a token expires.
                bearerToken = await TryUpgradeToBearerAsync(feed, response, repo, ct).ConfigureAwait(false);

                if (bearerToken == null) return accumulated;

                continue;
            }

            if (!response.IsSuccessStatusCode) return accumulated;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            AppendTags(accumulated, ParseRegistryTags(json), cap);

            if (accumulated.Count >= cap) return accumulated;

            if (!LinkHeaderParser.TryGetNextUri(response, currentUri, out var nextUri))
                return accumulated;

            currentUri = nextUri;
        }

        return accumulated;
    }

    private static void AppendTags(List<string> accumulated, List<string> page, int cap)
    {
        foreach (var tag in page)
        {
            if (accumulated.Count >= cap) return;

            accumulated.Add(tag);
        }
    }

    private async Task<string> TryUpgradeToBearerAsync(ExternalFeed feed, HttpResponseMessage challengeResponse, string repo, CancellationToken ct)
    {
        var scope = $"repository:{repo}:pull";

        if (!DockerRegistryAuthHelper.TryBuildDockerTokenEndpoint(challengeResponse, scope, out var tokenEndpoint))
            return null;

        var (success, token) = await RequestBearerTokenAsync(feed, tokenEndpoint, ct).ConfigureAwait(false);

        return success ? token : null;
    }

    private async Task<(bool Success, string Token)> RequestBearerTokenAsync(ExternalFeed feed, Uri tokenEndpoint, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(timeout: Timeout);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenEndpoint);

        if (DockerRegistryAuthHelper.HasCredentials(feed))
        {
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password));
        }

        using var tokenResponse = await client.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!tokenResponse.IsSuccessStatusCode) return (false, null);

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var token = DockerRegistryAuthHelper.ExtractBearerToken(tokenJson);

        return string.IsNullOrWhiteSpace(token) ? (false, null) : (true, token);
    }

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed, string bearerToken)
    {
        if (!string.IsNullOrEmpty(bearerToken))
            return new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" };

        if (!DockerRegistryAuthHelper.HasCredentials(feed)) return null;

        var encoded = DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password);

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }

    internal static Uri ResolveRegistryV2Base(Uri baseUri)
    {
        if (IsDockerHub(baseUri)) return DockerHubRegistryBaseUri;

        return ExternalFeedProbeUri.EnsureEndsWithPathSegment(baseUri, "v2");
    }

    internal static bool IsDockerHub(Uri baseUri) =>
        baseUri.Host.Contains("docker.io", StringComparison.OrdinalIgnoreCase) ||
        baseUri.Host.Contains("docker.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pure parser for one tags/list page. NO truncation here — pre-sort
    /// truncation was the cause of a real production bug where a freshly pushed
    /// <c>1.1.0</c> never appeared in dropdowns because Docker registries return
    /// tags in lexicographic order and 30 lex-earlier <c>1.0.x-N</c> tags filled
    /// the cap before semver sort. Ordering and take are now exclusively in
    /// <see cref="PackageVersionFilter.Apply"/>.
    /// </summary>
    internal static List<string> ParseRegistryTags(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<string>();

            foreach (var item in tags.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;

                result.Add(item.GetString());
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
