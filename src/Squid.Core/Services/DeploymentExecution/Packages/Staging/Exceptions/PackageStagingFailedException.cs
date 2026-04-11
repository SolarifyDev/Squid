namespace Squid.Core.Services.DeploymentExecution.Packages.Staging.Exceptions;

/// <summary>
/// Thrown by <see cref="PackageStagingPlanner"/> when no registered
/// <see cref="IPackageStagingHandler"/> could produce a plan for a given
/// <see cref="PackageRequirement"/>.
/// </summary>
public class PackageStagingFailedException : InvalidOperationException
{
    public string PackageId { get; }
    public string Version { get; }

    public PackageStagingFailedException(string packageId, string version, string message)
        : base(message)
    {
        PackageId = packageId;
        Version = version;
    }
}
