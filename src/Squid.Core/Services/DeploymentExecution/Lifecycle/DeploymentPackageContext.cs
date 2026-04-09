using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

/// <summary>
/// Encapsulates all package acquisition data emitted through the deployment lifecycle.
/// Mirrors the scope of Octopus's <c>PackageAcquisitionContext</c> but is constructed
/// server-side (Squid) rather than per-target (Octopus).
/// </summary>
/// <param name="SelectedPackages">All packages selected for this release.</param>
/// <param name="PackageId">The package ID being acquired in this event (per-package events only).</param>
/// <param name="PackageVersion">The package version.</param>
/// <param name="PackageFeedId">The feed ID from which to acquire the package.</param>
/// <param name="PackageSizeBytes">The downloaded package size in bytes.</param>
/// <param name="PackageHash">The MD5 hash of the downloaded package.</param>
/// <param name="PackageLocalPath">The local path to the downloaded package file.</param>
/// <param name="PackageIndex">Zero-based index of this package within the selected packages list.</param>
/// <param name="PackageCount">Total number of packages being acquired.</param>
/// <param name="PackageTotalSizeBytes">Cumulative size of all packages acquired so far (used in PackagesAcquiredEvent).</param>
/// <param name="PackageError">Error message if acquisition failed for this package.</param>
public record DeploymentPackageContext(
    List<ReleaseSelectedPackage> SelectedPackages,
    string PackageId,
    string PackageVersion,
    int PackageFeedId,
    long PackageSizeBytes,
    string PackageHash,
    string PackageLocalPath,
    int PackageIndex,
    int PackageCount,
    long PackageTotalSizeBytes,
    string PackageError);
