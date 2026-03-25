using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

public class DockerPackageSearchStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageSearchStrategy
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) => DockerRegistryAuthHelper.IsContainerRegistryFeed(new ExternalFeed { FeedType = feedType });

    public async Task<List<string>> SearchAsync(ExternalFeed feed, string query, int take, CancellationToken ct)
    {
        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri))
            return [];

        if (IsDockerHub(baseUri))
            return await SearchDockerHubAsync(feed, query, take, ct).ConfigureAwait(false);

        return await SearchGenericRegistryAsync(feed, baseUri, query, take, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> SearchDockerHubAsync(ExternalFeed feed, string query, int take, CancellationToken ct)
    {
        var ns = ResolveNamespace(feed);

        var effectiveQuery = !string.IsNullOrWhiteSpace(ns) && query.StartsWith($"{ns}/", StringComparison.OrdinalIgnoreCase)
            ? query[($"{ns}/".Length)..]
            : query;

        var url = string.IsNullOrWhiteSpace(ns)
            ? $"https://hub.docker.com/v2/search/repositories/?query={Uri.EscapeDataString(effectiveQuery)}&page_size={take}"
            : $"https://hub.docker.com/v2/namespaces/{Uri.EscapeDataString(ns)}/repositories/?page_size={take}&name={Uri.EscapeDataString(effectiveQuery)}";

        var headers = await BuildDockerHubAuthHeadersAsync(feed, ct).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(ns)
            ? ParseDockerHubSearchResults(json)
            : ParseDockerHubNamespaceResults(json, ns);
    }

    private async Task<List<string>> SearchGenericRegistryAsync(ExternalFeed feed, Uri baseUri, string query, int take, CancellationToken ct)
    {
        var catalogUri = ExternalFeedProbeUri.EnsureEndsWithPathSegment(baseUri, "v2");
        var catalogUrl = $"{catalogUri.ToString().TrimEnd('/')}/_catalog?n=200";

        var headers = BuildAuthHeaders(feed);
        var client = httpClientFactory.CreateClient(timeout: SearchTimeout, headers: headers);

        using var response = await client.GetAsync(catalogUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && DockerRegistryAuthHelper.HasCredentials(feed))
        {
            var bearerResult = await TrySearchWithBearerTokenAsync(feed, catalogUrl, response, query, take, ct).ConfigureAwait(false);

            if (bearerResult != null)
                return bearerResult;
        }

        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return FilterRepositories(json, query, take);
    }

    private async Task<List<string>> TrySearchWithBearerTokenAsync(ExternalFeed feed, string catalogUrl, HttpResponseMessage challengeResponse, string query, int take, CancellationToken ct)
    {
        if (!DockerRegistryAuthHelper.TryBuildDockerTokenEndpoint(challengeResponse, "registry:catalog:*", out var tokenEndpoint))
            return null;

        var tokenResult = await DockerRegistryAuthHelper.RequestBearerTokenAsync(httpClientFactory, tokenEndpoint, feed.Username, feed.Password, ct).ConfigureAwait(false);

        if (!tokenResult.Success)
            return null;

        var client = httpClientFactory.CreateClient(timeout: SearchTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return FilterRepositories(json, query, take);
    }

    private static Dictionary<string, string> BuildAuthHeaders(ExternalFeed feed)
    {
        if (!DockerRegistryAuthHelper.HasCredentials(feed))
            return null;

        var encoded = DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password);

        return new Dictionary<string, string> { ["Authorization"] = $"Basic {encoded}" };
    }

    private async Task<Dictionary<string, string>> BuildDockerHubAuthHeadersAsync(ExternalFeed feed, CancellationToken ct)
    {
        if (!DockerRegistryAuthHelper.HasCredentials(feed))
            return null;

        var token = await LoginDockerHubAsync(feed.Username, feed.Password, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(token))
            return null;

        return new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
    }

    private async Task<string> LoginDockerHubAsync(string username, string password, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(timeout: SearchTimeout);
        var payload = JsonSerializer.Serialize(new { username, password });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("https://hub.docker.com/v2/users/login/", content, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveNamespace(ExternalFeed feed)
    {
        var registryPath = ExternalFeedProperties.GetRegistryPath(feed)?.Trim().Trim('/');
        if (!string.IsNullOrWhiteSpace(registryPath))
            return registryPath;

        var username = feed.Username?.Trim();
        if (!string.IsNullOrWhiteSpace(username))
            return username;

        return null;
    }

    private static bool IsDockerHub(Uri baseUri) =>
        baseUri.Host.Contains("docker.io", StringComparison.OrdinalIgnoreCase) ||
        baseUri.Host.Contains("docker.com", StringComparison.OrdinalIgnoreCase);

    private static List<string> ParseDockerHubSearchResults(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            var packages = new List<string>();

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("repo_name", out var repoName) && repoName.ValueKind == JsonValueKind.String)
                    packages.Add(repoName.GetString());
            }

            return packages;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> ParseDockerHubNamespaceResults(string json, string ns)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            var packages = new List<string>();

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    packages.Add($"{ns}/{name.GetString()}");
            }

            return packages;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> FilterRepositories(string json, string query, int take)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array)
                return [];

            var packages = new List<string>();

            foreach (var item in repositories.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;

                var name = item.GetString();

                if (string.IsNullOrWhiteSpace(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    packages.Add(name);

                if (packages.Count >= take) break;
            }

            return packages;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
