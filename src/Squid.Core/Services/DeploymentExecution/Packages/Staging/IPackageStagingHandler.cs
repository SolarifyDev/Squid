using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// A single strategy for staging a package on a deployment target. Handlers are
/// ordered by <see cref="Priority"/> (descending) inside <see cref="PackageStagingPlanner"/>;
/// the first whose <see cref="CanHandle"/> returns true and whose <see cref="TryPlanAsync"/>
/// returns a non-null plan wins.
/// </summary>
public interface IPackageStagingHandler : IScopedDependency
{
    /// <summary>
    /// Relative priority when multiple handlers are registered. Higher values run
    /// first. CacheHit &gt; RemoteDownload &gt; Delta &gt; FullUpload by convention.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Returns true when the handler is applicable to the given requirement
    /// and transport context. Should be a cheap, synchronous check.
    /// </summary>
    bool CanHandle(PackageRequirement requirement, PackageStagingContext context);

    /// <summary>
    /// Attempts to stage the package and produce a plan. Returns <c>null</c> when
    /// the handler is applicable in principle but could not produce a plan this
    /// time (e.g. cache miss) — the planner will then try the next handler.
    /// </summary>
    Task<PackageStagingPlan> TryPlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct);
}
