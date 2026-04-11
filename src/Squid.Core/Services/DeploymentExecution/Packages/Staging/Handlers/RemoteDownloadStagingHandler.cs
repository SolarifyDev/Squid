namespace Squid.Core.Services.DeploymentExecution.Packages.Staging.Handlers;

/// <summary>
/// Stub handler reserved for the future "target fetches package directly from
/// an upstream feed" strategy. Currently always declines via
/// <see cref="CanHandle"/> so the planner falls through to the next handler.
/// Kept in the chain to document the extensibility point and lock in the
/// priority ordering (between <see cref="CacheHitStagingHandler"/> and
/// <see cref="FullUploadStagingHandler"/>).
/// </summary>
public class RemoteDownloadStagingHandler : IPackageStagingHandler
{
    public int Priority => 80;

    public bool CanHandle(PackageRequirement requirement, PackageStagingContext context) => false;

    public Task<PackageStagingPlan> TryPlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct)
        => Task.FromResult<PackageStagingPlan>(null);
}
