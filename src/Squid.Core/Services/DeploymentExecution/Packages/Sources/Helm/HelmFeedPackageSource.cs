using Serilog;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.Helm;

public class HelmFeedPackageSource(ISquidHttpClientFactory httpClientFactory, FeedPackageDownloader downloader) : IFeedPackageSource
{
    public bool CanHandle(string feedType) => PackageFeedKinds.Classify(feedType) == PackageFeedKind.Helm;

    public Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct) =>
        downloader.FetchAsync(feed, packageId, version, () => ResolveChartUrlAsync(feed, packageId, version, ct), ct);

    private async Task<string> ResolveChartUrlAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var baseUri = (feed.FeedUri ?? string.Empty).TrimEnd('/');

        try
        {
            var chartUrl = await FindChartUrlInIndexAsync(feed, packageId, version, baseUri, ct).ConfigureAwait(false);

            if (chartUrl != null)
                return chartUrl;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Failed to parse Helm index.yaml, falling back to convention URL");
        }

        return $"{baseUri}/{packageId}-{version}.tgz";
    }

    private async Task<string> FindChartUrlInIndexAsync(ExternalFeed feed, string packageId, string version, string baseUri, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(timeout: TimeSpan.FromSeconds(30), headers: FeedAuthHeaders.Build(feed));
        var response = await client.GetAsync($"{baseUri}/index.yaml", ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[Deploy] Failed to fetch Helm index.yaml: HTTP {StatusCode}, falling back to convention URL", (int)response.StatusCode);

            return null;
        }

        var indexContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var chartUrl = HelmIndexParser.ParseChartUrl(indexContent, packageId, version, baseUri);

        if (chartUrl == null)
            Log.Warning("[Deploy] Chart {PackageId} v{Version} not found in Helm index, falling back to convention URL", packageId, version);

        return chartUrl;
    }
}
