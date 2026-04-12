using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// Resolves the best <see cref="IPackageStagingHandler"/> for a given
/// <see cref="PackageRequirement"/> and returns the resulting
/// <see cref="PackageStagingPlan"/>. This is the single seam that transports
/// use to materialise packages — today SSH consults it via
/// <c>SshExecutionStrategy.UploadAndExtractPackages</c>.
/// </summary>
public interface IPackageStagingPlanner : IScopedDependency
{
    Task<PackageStagingPlan> PlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct);
}
