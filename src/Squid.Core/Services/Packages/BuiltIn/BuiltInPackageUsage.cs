using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Packages.BuiltIn;

public sealed class BuiltInPackageUsage(IRepository repository) : IBuiltInPackageUsage
{
    public Task<bool> IsVersionSelectedByReleaseAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        // Package ids are ASCII, so lowering with the invariant culture here and with lower() in the database agree whatever the server's culture is.
        var lowered = packageId.ToLowerInvariant();

        return repository.QueryNoTracking<ReleaseSelectedPackage>(p => p.FeedId == feedId && p.Version == version && p.PackageReferenceName.ToLower() == lowered).AnyAsync(ct);
    }
}
