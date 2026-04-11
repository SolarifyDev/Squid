namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// The materialised outcome of staging a single package on a deployment target.
/// Produced by a handler and returned from <see cref="IPackageStagingPlanner.PlanAsync"/>.
/// Contains everything downstream extract/unpack steps need to locate the package bytes.
/// </summary>
/// <param name="Strategy">The strategy actually used (cache-hit, full-upload, ...).</param>
/// <param name="PackageId">Echoed from the originating <see cref="PackageRequirement"/>.</param>
/// <param name="Version">Echoed from the originating <see cref="PackageRequirement"/>.</param>
/// <param name="RemotePath">Final path of the package archive on the target (e.g. the remote nupkg path).</param>
/// <param name="LocalPath">Local path of the package bytes on the Squid server, if still needed downstream; null for cache hits.</param>
/// <param name="Hash">MD5 hash of the staged package, if verified; null when unknown.</param>
public sealed record PackageStagingPlan(
    PackageStagingStrategy Strategy,
    string PackageId,
    string Version,
    string RemotePath,
    string LocalPath,
    string Hash);
