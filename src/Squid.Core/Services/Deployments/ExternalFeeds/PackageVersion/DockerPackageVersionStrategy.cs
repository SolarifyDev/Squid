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

    public bool CanHandle(string feedType) => DockerRegistryAuthHelper.IsContainerRegistryFeed(new ExternalFeed { FeedType = feedType });

    public async Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return [];

        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri)) return [];

        var v2Base = ResolveRegistryV2Base(baseUri);
        var repo = IsDockerHub(baseUri) && !packageId.Contains('/') ? $"library/{packageId}" : packageId;

        return await ListRegistryTagsAsync(feed, v2Base, repo, take, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> ListRegistryTagsAsync(ExternalFeed feed, Uri v2Base, string repo, int take, CancellationToken ct)
    {
        var tagsUrl = $"{v2Base.ToString().TrimEnd('/')}/{repo}/tags/list";

        var headers = BuildAuthHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        using var response = await client.GetAsync(tagsUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var bearerResult = await TryListWithBearerTokenAsync(feed, tagsUrl, response, repo, take, ct).ConfigureAwait(false);

            if (bearerResult != null) return bearerResult;
        }

        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseRegistryTags(json, take);
    }

    private async Task<List<string>> TryListWithBearerTokenAsync(ExternalFeed feed, string tagsUrl, HttpResponseMessage challengeResponse, string repo, int take, CancellationToken ct)
    {
        var scope = $"repository:{repo}:pull";

        if (!DockerRegistryAuthHelper.TryBuildDockerTokenEndpoint(challengeResponse, scope, out var tokenEndpoint))
            return null;

        var tokenResult = await RequestBearerTokenAsync(feed, tokenEndpoint, ct).ConfigureAwait(false);

        if (!tokenResult.Success) return null;

        var client = httpClientFactory.CreateClient(timeout: Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParseRegistryTags(json, take);
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

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
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

    internal static List<string> ParseRegistryTags(string json, int take)
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

                if (result.Count >= take) break;
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
