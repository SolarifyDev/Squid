namespace Squid.Core.Services.DeploymentExecution.Packages.Staging.Handlers;

/// <summary>
/// Stub handler reserved for the future "binary delta patching" strategy.
/// Currently always declines via <see cref="CanHandle"/>. Kept in the chain
/// to document the extensibility point and lock in its priority (sits between
/// <see cref="RemoteDownloadStagingHandler"/> and
/// <see cref="FullUploadStagingHandler"/>).
/// </summary>
public class DeltaStagingHandler : IPackageStagingHandler
{
    public int Priority => 70;

    public bool CanHandle(PackageRequirement requirement, PackageStagingContext context) => false;

    public Task<PackageStagingPlan> TryPlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct)
        => Task.FromResult<PackageStagingPlan>(null);
}
