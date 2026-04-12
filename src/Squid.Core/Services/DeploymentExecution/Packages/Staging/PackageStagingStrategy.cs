namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// The strategy chosen by the <see cref="IPackageStagingPlanner"/> for materialising a
/// single package on a deployment target. Handlers return one of these values to
/// describe how the package was (or will be) placed at its final remote path.
/// </summary>
public enum PackageStagingStrategy
{
    /// <summary>
    /// The package was fully uploaded from the server to the target.
    /// </summary>
    FullUpload = 0,

    /// <summary>
    /// A matching package already exists on the target — no upload required.
    /// </summary>
    CacheHit = 1,

    /// <summary>
    /// The target will fetch the package directly from an upstream feed
    /// (e.g. a shared artifact store reachable from the target).
    /// </summary>
    RemoteDownload = 2,

    /// <summary>
    /// Only a binary delta was transmitted; the target reconstructs the full
    /// package from a previously cached base version.
    /// </summary>
    Delta = 3
}
