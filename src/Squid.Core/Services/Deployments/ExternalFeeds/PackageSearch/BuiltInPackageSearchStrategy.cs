using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Constants;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

/// <summary>Finds built-in feed packages whose id contains the query. The feed type is matched exactly.</summary>
public class BuiltInPackageSearchStrategy(IBuiltInPackageDataProvider packages) : IPackageSearchStrategy
{
    private const int DefaultTake = 20;

    public bool CanHandle(string feedType) => BuiltInFeed.IsBuiltIn(feedType);

    public async Task<List<string>> SearchAsync(ExternalFeed feed, string query, int take, CancellationToken ct)
    {
        var found = await packages.ListPackagesAsync(feed.Id, query, take <= 0 ? DefaultTake : take, ct).ConfigureAwait(false);

        return found.Select(p => p.PackageId).ToList();
    }
}
