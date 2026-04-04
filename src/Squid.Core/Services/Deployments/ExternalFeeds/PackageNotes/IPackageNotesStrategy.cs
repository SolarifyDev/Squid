using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

public interface IPackageNotesStrategy : IScopedDependency
{
    bool CanHandle(string feedType);

    Task<PackageNotesResult> GetNotesAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct);
}
