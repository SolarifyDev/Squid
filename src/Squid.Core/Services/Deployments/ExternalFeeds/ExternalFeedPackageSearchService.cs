using Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedPackageSearchService : IScopedDependency
{
    Task<SearchFeedPackagesResponseData> SearchAsync(int feedId, string query, int take, CancellationToken ct);
}

public class ExternalFeedPackageSearchService(
    IExternalFeedDataProvider dataProvider,
    IEnumerable<IPackageSearchStrategy> strategies) : IExternalFeedPackageSearchService
{
    public async Task<SearchFeedPackagesResponseData> SearchAsync(int feedId, string query, int take, CancellationToken ct)
    {
        var feed = await dataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

        if (feed == null)
            return new SearchFeedPackagesResponseData { Packages = [] };

        if (string.IsNullOrWhiteSpace(feed.FeedUri))
            return new SearchFeedPackagesResponseData { Packages = [] };

        var strategy = strategies.FirstOrDefault(s => s.CanHandle(feed.FeedType));

        if (strategy == null)
            return new SearchFeedPackagesResponseData { Packages = [] };

        var packages = await strategy.SearchAsync(feed, query, take, ct).ConfigureAwait(false);

        return new SearchFeedPackagesResponseData { Packages = packages };
    }
}
