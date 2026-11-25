using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Generic;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public class RoutingPackageContentFetcher(IEnumerable<IFeedPackageSource> sources, GenericFeedPackageSource fallback) : IPackageContentFetcher
{
    public Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var source = sources.SingleOrDefault(s => s.CanHandle(feed.FeedType));

        return source != null
            ? source.FetchAsync(feed, packageId, version, ct)
            : fallback.FetchAsync(feed, packageId, version, ct);
    }
}
