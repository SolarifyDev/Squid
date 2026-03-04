using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    public static bool ShouldExecuteStep(
        DeploymentStepDto step,
        HashSet<string> targetRoles,
        bool previousStepSucceeded,
        List<VariableDto> effectiveVariables = null)
        => StepEligibilityEvaluator.ShouldExecuteStep(step, targetRoles, previousStepSucceeded, effectiveVariables);

    public static bool ShouldExecuteAction(
        DeploymentActionDto action,
        int deploymentEnvironmentId,
        int deploymentChannelId)
        => StepEligibilityEvaluator.ShouldExecuteAction(action, deploymentEnvironmentId, deploymentChannelId);
}
