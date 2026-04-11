namespace Squid.Core.Services.DeploymentExecution.Planning.Exceptions;

/// <summary>
/// Thrown by <see cref="IDeploymentPlanner"/> when a plan built in
/// <see cref="PlanMode.Execute"/> has at least one entry in
/// <see cref="DeploymentPlan.BlockingReasons"/>. Callers in Preview mode never see this —
/// they consume the plan directly and render its reasons to the UI.
/// </summary>
public sealed class DeploymentPlanValidationException : InvalidOperationException
{
    public DeploymentPlanValidationException(DeploymentPlan plan)
        : base(BuildMessage(plan))
    {
        Plan = plan;
    }

    /// <summary>The plan that triggered the exception. <see cref="DeploymentPlan.BlockingReasons"/> is non-empty.</summary>
    public DeploymentPlan Plan { get; }

    /// <summary>Convenience accessor for the plan's blockers.</summary>
    public IReadOnlyList<PlanBlockingReason> Reasons => Plan.BlockingReasons;

    private static string BuildMessage(DeploymentPlan plan)
    {
        if (plan.BlockingReasons.Count == 0)
            return "Deployment plan is blocked but no reasons were supplied.";

        var summary = string.Join("; ", plan.BlockingReasons.Select(r => $"[{r.Code}] {r.Message}"));

        return $"Deployment plan is blocked: {summary}";
    }
}
