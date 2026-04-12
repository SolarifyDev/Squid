using Serilog;
using Squid.Core.Services.DeploymentExecution.Packages.Staging.Exceptions;

namespace Squid.Core.Services.DeploymentExecution.Packages.Staging;

/// <summary>
/// Dispatches package staging requests to the first matching
/// <see cref="IPackageStagingHandler"/> (ordered by <see cref="IPackageStagingHandler.Priority"/>
/// descending). Throws <see cref="PackageStagingFailedException"/> when no
/// handler produces a plan.
/// </summary>
public class PackageStagingPlanner : IPackageStagingPlanner
{
    private readonly IReadOnlyList<IPackageStagingHandler> _handlers;

    public PackageStagingPlanner(IEnumerable<IPackageStagingHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = handlers
            .OrderByDescending(h => h.Priority)
            .ToList();
    }

    public async Task<PackageStagingPlan> PlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var handler in _handlers)
        {
            if (!handler.CanHandle(requirement, context)) continue;

            var plan = await handler.TryPlanAsync(requirement, context, ct).ConfigureAwait(false);

            if (plan == null)
            {
                Log.Debug("[Staging] Handler {Handler} declined to plan {PackageId} v{Version}, trying next",
                    handler.GetType().Name, requirement.PackageId, requirement.Version);
                continue;
            }

            Log.Information("[Staging] {Handler} produced {Strategy} plan for {PackageId} v{Version}",
                handler.GetType().Name, plan.Strategy, requirement.PackageId, requirement.Version);

            return plan;
        }

        throw new PackageStagingFailedException(
            requirement.PackageId,
            requirement.Version,
            $"No staging handler produced a plan for package {requirement.PackageId} v{requirement.Version} on transport {context.CommunicationStyle}");
    }
}
