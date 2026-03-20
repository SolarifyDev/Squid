using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageSearch;

public interface IPackageSearchStrategy : IScopedDependency
{
    bool CanHandle(string feedType);
    Task<List<string>> SearchAsync(ExternalFeed feed, string query, int take, CancellationToken ct);
}
