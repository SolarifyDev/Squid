using Serilog;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

/// <summary>
/// The download half shared by every HTTP-backed source: resolve the URL, GET it with the feed's auth
/// headers, unpack the archive. Failures become warnings on the result, never exceptions.
/// </summary>
public class FeedPackageDownloader(ISquidHttpClientFactory httpClientFactory) : IScopedDependency
{
    public async Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, Func<Task<string>> resolveUrl, CancellationToken ct)
    {
        var warnings = new List<string>();

        try
        {
            return await DownloadAsync(feed, resolveUrl, warnings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to fetch package {PackageId} v{Version} from feed {FeedId}", packageId, version, feed.Id);
            warnings.Add($"Package fetch failed: {ex.Message}");

            return EmptyResult(warnings);
        }
    }

    private async Task<PackageFetchResult> DownloadAsync(ExternalFeed feed, Func<Task<string>> resolveUrl, List<string> warnings, CancellationToken ct)
    {
        var url = await resolveUrl().ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(timeout: TimeSpan.FromMinutes(5), headers: FeedAuthHeaders.Build(feed));

        Log.Information("[Deploy] Downloading package from {Url}", url);
        var response = await client.GetAsync(url, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            warnings.Add($"Package download failed: HTTP {(int)response.StatusCode} from {url}");

            return EmptyResult(warnings);
        }

        var archiveBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var files = PackageArchiveExtractor.Extract(archiveBytes);

        Log.Information("[Deploy] Extracted {FileCount} files from package", files.Count);

        return new PackageFetchResult(files, warnings, archiveBytes);
    }

    private static PackageFetchResult EmptyResult(List<string> warnings) => new(new Dictionary<string, byte[]>(), warnings, Array.Empty<byte>());
}
