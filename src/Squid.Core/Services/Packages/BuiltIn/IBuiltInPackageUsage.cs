using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Whether a built-in package version is relied on by a release. The one place that decides it, for replacing now and for renaming and deleting later.</summary>
public interface IBuiltInPackageUsage : IScopedDependency
{
    /// <summary>
    /// True when any release of the feed's space selects this exact version: a <see cref="ReleaseSelectedPackage"/> with the feed, the package id (ignoring case)
    /// and the version. The selection is what a release records and what a deployment later fetches, so a process snapshot adds nothing to it: it names the
    /// package and feed but never a version.
    /// </summary>
    Task<bool> IsVersionSelectedByReleaseAsync(int feedId, string packageId, string version, CancellationToken ct);
}

public sealed class BuiltInPackageUsage(IRepository repository) : IBuiltInPackageUsage
{
    public Task<bool> IsVersionSelectedByReleaseAsync(int feedId, string packageId, string version, CancellationToken ct)
    {
        var lowered = packageId.ToLower();

        return repository.QueryNoTracking<ReleaseSelectedPackage>(p => p.FeedId == feedId && p.Version == version && p.PackageReferenceName.ToLower() == lowered).AnyAsync(ct);
    }
}
