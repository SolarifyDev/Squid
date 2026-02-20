using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    public static bool ShouldExecuteStep(
        DeploymentStepDto step,
        HashSet<string> targetRoles,
        bool previousStepSucceeded)
    {
        if (step.IsDisabled)
            return false;

        if (!EvaluateCondition(step.Condition, previousStepSucceeded))
            return false;

        if (!MatchesTargetRoles(step, targetRoles))
            return false;

        return true;
    }

    public static bool ShouldExecuteAction(
        DeploymentActionDto action,
        int deploymentEnvironmentId,
        int deploymentChannelId)
    {
        if (action.IsDisabled)
            return false;

        if (!AppliesToEnvironment(action, deploymentEnvironmentId))
            return false;

        if (action.Channels != null && action.Channels.Count > 0
            && !action.Channels.Contains(deploymentChannelId))
            return false;

        return true;
    }

    private static bool AppliesToEnvironment(DeploymentActionDto action, int environmentId)
    {
        var hasInclusion = action.Environments != null && action.Environments.Count > 0;
        var hasExclusion = action.ExcludedEnvironments != null && action.ExcludedEnvironments.Count > 0;

        if (!hasInclusion && !hasExclusion)
            return true;

        if (hasExclusion && action.ExcludedEnvironments.Contains(environmentId))
            return false;

        if (hasInclusion && !action.Environments.Contains(environmentId))
            return false;

        return true;
    }

    private static bool EvaluateCondition(string condition, bool previousStepSucceeded)
    {
        return condition switch
        {
            "Always" => true,
            "Failure" => !previousStepSucceeded,
            "Variable" => true,
            null or "" => previousStepSucceeded,
            _ => previousStepSucceeded // "Success" and any unknown value
        };
    }

    private static bool MatchesTargetRoles(DeploymentStepDto step, HashSet<string> targetRoles)
    {
        if (targetRoles == null)
            return true;

        var stepRolesProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == DeploymentVariables.Action.TargetRoles);

        if (stepRolesProperty == null || string.IsNullOrEmpty(stepRolesProperty.PropertyValue))
            return true;

        var stepRoles = DeploymentTargetFinder.ParseRoles(stepRolesProperty.PropertyValue);
        return stepRoles.Overlaps(targetRoles);
    }
}
