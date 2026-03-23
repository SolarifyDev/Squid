using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

public interface IPackageVersionStrategy : IScopedDependency
{
    bool CanHandle(string feedType);

    Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, int take, CancellationToken ct);
}
