namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// Resolves a <see cref="DeploymentPlanRequest"/> into a <see cref="DeploymentPlan"/> by
/// walking the deployment process, filtering targets per step, and validating capabilities
/// on every dispatch.
///
/// <para>
/// Phase 6 introduces this as the single source of truth for both Preview and Execute:
/// Preview code calls the planner and renders the plan to the UI, while the executor
/// calls the planner in <see cref="PlanMode.Execute"/> and walks the resulting dispatches.
/// </para>
/// </summary>
public interface IDeploymentPlanner : IScopedDependency
{
    /// <summary>
    /// Build a plan from <paramref name="request"/>. In <see cref="PlanMode.Preview"/>
    /// blockers are surfaced via <see cref="DeploymentPlan.BlockingReasons"/>. In
    /// <see cref="PlanMode.Execute"/> a non-empty blocker list throws
    /// <see cref="Exceptions.DeploymentPlanValidationException"/>.
    /// </summary>
    Task<DeploymentPlan> PlanAsync(DeploymentPlanRequest request, CancellationToken ct);
}
