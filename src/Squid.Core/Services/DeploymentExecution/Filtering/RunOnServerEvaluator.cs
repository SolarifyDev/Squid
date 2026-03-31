using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class RunOnServerEvaluator
{
    public static bool IsRunOnServer(DeploymentStepDto step)
    {
        var prop = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.RunOnServer);

        return string.Equals(prop?.PropertyValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEntireDeploymentServerOnly(List<DeploymentStepDto> steps, Func<DeploymentActionDto, ExecutionScope> scopeResolver)
    {
        if (steps == null || steps.Count == 0) return true;

        foreach (var step in steps)
        {
            if (step.IsDisabled) continue;
            if (IsRunOnServer(step)) continue;
            if (scopeResolver != null && IsStepLevelOnly(step, scopeResolver)) continue;

            return false;
        }

        return true;
    }

    private static bool IsStepLevelOnly(DeploymentStepDto step, Func<DeploymentActionDto, ExecutionScope> scopeResolver)
    {
        var enabledActions = step.Actions?.Where(a => !a.IsDisabled).ToList();

        if (enabledActions == null || enabledActions.Count == 0) return false;

        return enabledActions.All(a => scopeResolver(a) == ExecutionScope.StepLevel);
    }
}
