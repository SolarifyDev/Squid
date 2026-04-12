namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// Describes a package that needs to be materialised on a deployment target.
/// Consumed by <see cref="IPackageStagingPlanner"/> and its handlers.
/// </summary>
/// <param name="PackageId">Package identifier (e.g. <c>Acme.Web</c>).</param>
/// <param name="Version">Package version (e.g. <c>1.2.3</c>).</param>
/// <param name="LocalPath">Absolute path on the Squid server where the fetched package bytes live.</param>
/// <param name="SizeBytes">Package size in bytes, as reported by the acquisition step.</param>
/// <param name="Hash">MD5 hash of the local package bytes (hex, lower-case).</param>
public sealed record PackageRequirement(
    string PackageId,
    string Version,
    string LocalPath,
    long SizeBytes,
    string Hash);
