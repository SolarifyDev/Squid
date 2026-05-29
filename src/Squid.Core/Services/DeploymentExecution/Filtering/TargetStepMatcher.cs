using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class TargetStepMatcher
{
    /// <summary>
    /// Runtime fallback used only when no plan exists for a step. Delegates to
    /// <see cref="StepRoleMatcher"/> — the SAME role match the planner uses — so
    /// even this path can never diverge from what a preview would compute (e.g.
    /// it now treats a whitespace-only roles value as unscoped, matching the
    /// planner, instead of matching zero targets).
    /// </summary>
    public static List<DeploymentTargetContext> FindMatchingTargetsForStep(DeploymentStepDto step, List<DeploymentTargetContext> allTargets)
    {
        var requiredRoles = StepRoleMatcher.RequiredRoles(step);

        return allTargets
            .Where(tc => StepRoleMatcher.Matches(requiredRoles, DeploymentTargetFinder.ParseRoles(tc.Machine.Roles)))
            .ToList();
    }
}
