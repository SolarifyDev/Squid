namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// Determines how aggressively <see cref="IDeploymentPlanner"/> resolves a plan.
///
/// <para>
/// Both modes run the same applicability / role-matching / capability-validation pipeline.
/// The only differences are downstream: <see cref="Execute"/> throws a
/// <see cref="Exceptions.DeploymentPlanValidationException"/> when the returned plan has
/// any <see cref="DeploymentPlan.BlockingReasons"/>, while <see cref="Preview"/> never
/// throws — callers render the plan into a UI regardless of blocker state.
/// </para>
/// </summary>
public enum PlanMode
{
    /// <summary>Non-throwing — surfaces every blocker for the preview UI.</summary>
    Preview = 0,

    /// <summary>Throws <see cref="Exceptions.DeploymentPlanValidationException"/> on any blocker.</summary>
    Execute = 1
}
