using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Whether a built-in package version is relied on by a release. The one place that decides it, for replacing now and for renaming and deleting later.</summary>
public interface IBuiltInPackageUsage : IScopedDependency
{
    /// <summary>
    /// True when any release selects this exact version of the feed: a <see cref="ReleaseSelectedPackage"/> with the feed and the version whose reference name is the
    /// package id (ignoring case) or is not the id of any built-in package of the feed (empty, a variable or a typo could be this package; see
    /// <c>ReleaseSelectionSql</c>). The selection is what a release records and what a deployment later fetches, so a process snapshot adds nothing to it: it names the
    /// package and feed but never a version. <c>BuiltInPackageDataProvider.ReplaceContentAsync</c> uses the very same SQL predicate, atomically with its update.
    /// </summary>
    Task<bool> IsVersionSelectedByReleaseAsync(int feedId, string packageId, string version, CancellationToken ct);
}
