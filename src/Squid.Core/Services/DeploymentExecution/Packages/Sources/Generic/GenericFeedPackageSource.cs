using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.Generic;

/// <summary>
/// The explicit fallback for feed types no <see cref="IFeedPackageSource"/> claims: GET <c>{feed}/{id}/{version}</c>.
/// Deliberately not an <see cref="IFeedPackageSource"/>, so it never takes part in CanHandle matching.
/// </summary>
public class GenericFeedPackageSource(FeedPackageDownloader downloader) : IScopedDependency
{
    public Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct) =>
        downloader.FetchAsync(feed, packageId, version, () => Task.FromResult(BuildDownloadUrl(feed, packageId, version)), ct);

    internal static string BuildDownloadUrl(ExternalFeed feed, string packageId, string version)
    {
        var baseUri = (feed.FeedUri ?? string.Empty).TrimEnd('/');

        return string.IsNullOrEmpty(version) ? $"{baseUri}/{packageId}" : $"{baseUri}/{packageId}/{version}";
    }
}
