using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedPackageVersionService : IScopedDependency
{
    Task<SearchFeedPackageVersionsResponseData> ListVersionsAsync(int feedId, string packageId, int take, bool includePreRelease, string filter, CancellationToken ct);
}

public class ExternalFeedPackageVersionService(
    IExternalFeedDataProvider dataProvider,
    IEnumerable<IPackageVersionStrategy> strategies) : IExternalFeedPackageVersionService
{
    public async Task<SearchFeedPackageVersionsResponseData> ListVersionsAsync(int feedId, string packageId, int take, bool includePreRelease, string filter, CancellationToken ct)
    {
        var feed = await dataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

        if (feed == null || string.IsNullOrWhiteSpace(feed.FeedUri))
            return new SearchFeedPackageVersionsResponseData { Versions = [] };

        var strategy = strategies.FirstOrDefault(s => s.CanHandle(feed.FeedType));

        if (strategy == null)
            return new SearchFeedPackageVersionsResponseData { Versions = [] };

        var versions = await strategy.ListVersionsAsync(feed, packageId, take, ct).ConfigureAwait(false);

        versions = PackageVersionFilter.Apply(versions, includePreRelease, filter, take);

        return new SearchFeedPackageVersionsResponseData { Versions = versions };
    }
}
