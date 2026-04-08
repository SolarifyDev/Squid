using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public interface IPackageAcquisitionService : IScopedDependency
{
    Task<PackageAcquisitionResult> AcquireAsync(ExternalFeed feed, string packageId, string version, int deploymentId, CancellationToken ct);
}

public static class PackageAcquisitionServiceExtensions
{
    public static string BuildPackageStoragePath(int deploymentId)
        => Path.Combine(Path.GetTempPath(), "squid-packages", deploymentId.ToString());
}
