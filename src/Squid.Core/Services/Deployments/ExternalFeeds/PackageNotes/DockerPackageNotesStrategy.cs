using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

public class DockerPackageNotesStrategy(ISquidHttpClientFactory httpClientFactory) : IPackageNotesStrategy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool CanHandle(string feedType) => DockerRegistryAuthHelper.IsContainerRegistryFeed(new ExternalFeed { FeedType = feedType });

    public async Task<PackageNotesResult> GetNotesAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            return PackageNotesResult.Empty();

        if (!ExternalFeedProbeUri.TryNormalize(feed.FeedUri, out var baseUri))
            return PackageNotesResult.Failure("Invalid feed URI");

        var v2Base = DockerPackageVersionStrategy.ResolveRegistryV2Base(baseUri);
        var repo = DockerPackageVersionStrategy.IsDockerHub(baseUri) && !packageId.Contains('/') ? $"library/{packageId}" : packageId;

        return await FetchNotesAsync(feed, v2Base, repo, version, ct).ConfigureAwait(false);
    }

    private async Task<PackageNotesResult> FetchNotesAsync(ExternalFeed feed, Uri v2Base, string repo, string version, CancellationToken ct)
    {
        var baseUrl = v2Base.ToString().TrimEnd('/');

        // Step 1: Obtain bearer token by probing /v2/ (handles registries that return 404 instead of 401 on manifest)
        var bearerToken = await ObtainBearerTokenAsync(feed, baseUrl, repo, ct).ConfigureAwait(false);

        // Step 2: Request manifest with token (or without if no auth needed)
        var manifestUrl = $"{baseUrl}/{repo}/manifests/{version}";

        var client = httpClientFactory.CreateClient(timeout: Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        AddManifestAcceptHeaders(request);

        if (bearerToken != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        else if (DockerRegistryAuthHelper.HasCredentials(feed))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", DockerRegistryAuthHelper.ToBasicAuthValue(feed.Username, feed.Password));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return PackageNotesResult.Failure($"Registry returned {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return await ResolveNotesFromManifestAsync(json, baseUrl, repo, bearerToken, ct).ConfigureAwait(false);
    }

    private async Task<string> ObtainBearerTokenAsync(ExternalFeed feed, string baseUrl, string repo, CancellationToken ct)
    {
        // Probe /v2/ to trigger WWW-Authenticate challenge
        var probeClient = httpClientFactory.CreateClient(timeout: Timeout);

        using var probeResponse = await probeClient.GetAsync($"{baseUrl}/", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (probeResponse.StatusCode != HttpStatusCode.Unauthorized) return null;

        var scope = $"repository:{repo}:pull";

        if (!DockerRegistryAuthHelper.TryBuildDockerTokenEndpoint(probeResponse, scope, out var tokenEndpoint))
            return null;

        var tokenResult = await DockerRegistryAuthHelper.RequestBearerTokenAsync(httpClientFactory, tokenEndpoint, feed.Username, feed.Password, ct).ConfigureAwait(false);

        return tokenResult.Success ? tokenResult.Token : null;
    }

    private async Task<PackageNotesResult> ResolveNotesFromManifestAsync(string manifestJson, string baseUrl, string repo, string bearerToken, CancellationToken ct)
    {
        // Try V2/OCI manifest → config blob approach first
        var configDigest = ExtractConfigDigest(manifestJson);

        if (configDigest != null)
        {
            var configResult = await FetchConfigBlobAsync(baseUrl, repo, configDigest, bearerToken, ct).ConfigureAwait(false);

            if (configResult != null) return configResult;
        }

        // Fallback: V1 manifest with history[].v1Compatibility
        return ParseV1ManifestNotes(manifestJson);
    }

    private async Task<PackageNotesResult> FetchConfigBlobAsync(string baseUrl, string repo, string configDigest, string bearerToken, CancellationToken ct)
    {
        var blobUrl = $"{baseUrl}/{repo}/blobs/{configDigest}";

        var headers = bearerToken != null
            ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearerToken}" }
            : null;

        var client = httpClientFactory.CreateClient(timeout: Timeout, headers: headers);

        try
        {
            using var response = await client.GetAsync(blobUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return ParseConfigBlob(json);
        }
        catch
        {
            return null;
        }
    }

    internal static string ExtractConfigDigest(string manifestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("config", out var config) && config.TryGetProperty("digest", out var digest))
                return digest.GetString();

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static PackageNotesResult ParseConfigBlob(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTimeOffset? published = null;

            if (root.TryGetProperty("created", out var createdProp) && DateTimeOffset.TryParse(createdProp.GetString(), out var createdDate))
                published = createdDate;

            var os = root.TryGetProperty("os", out var osProp) ? osProp.GetString() : null;
            var arch = root.TryGetProperty("architecture", out var archProp) ? archProp.GetString() : null;

            string notes = null;

            if (!string.IsNullOrEmpty(os) || !string.IsNullOrEmpty(arch))
                notes = $"Platform: {os} {arch}".Trim();

            if (notes == null && published == null) return PackageNotesResult.Empty();

            return PackageNotesResult.Success(notes, published);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static PackageNotesResult ParseV1ManifestNotes(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("history", out var history) || history.GetArrayLength() == 0)
                return PackageNotesResult.Empty();

            var firstEntry = history[0];

            if (!firstEntry.TryGetProperty("v1Compatibility", out var v1Compat))
                return PackageNotesResult.Empty();

            return ParseV1Compatibility(v1Compat.GetString());
        }
        catch (JsonException)
        {
            return PackageNotesResult.Failure("Failed to parse manifest JSON");
        }
    }

    internal static PackageNotesResult ParseV1Compatibility(string v1CompatJson)
    {
        if (string.IsNullOrEmpty(v1CompatJson)) return PackageNotesResult.Empty();

        try
        {
            using var doc = JsonDocument.Parse(v1CompatJson);
            var root = doc.RootElement;

            DateTimeOffset? published = null;

            if (root.TryGetProperty("created", out var createdProp) && DateTimeOffset.TryParse(createdProp.GetString(), out var createdDate))
                published = createdDate;

            var os = root.TryGetProperty("os", out var osProp) ? osProp.GetString() : null;
            var arch = root.TryGetProperty("architecture", out var archProp) ? archProp.GetString() : null;

            string notes = null;

            if (!string.IsNullOrEmpty(os) || !string.IsNullOrEmpty(arch))
                notes = $"Platform: {os} {arch}".Trim();

            if (notes == null && published == null) return PackageNotesResult.Empty();

            return PackageNotesResult.Success(notes, published);
        }
        catch (JsonException)
        {
            return PackageNotesResult.Empty();
        }
    }

    private static void AddManifestAcceptHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v1+json"));
    }
}
