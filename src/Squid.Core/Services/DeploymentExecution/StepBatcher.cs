using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution;

public static class StepBatcher
{
    public static List<List<DeploymentStepDto>> BatchSteps(List<DeploymentStepDto> steps)
    {
        var batches = new List<List<DeploymentStepDto>>();

        if (steps == null || steps.Count == 0)
            return batches;

        List<DeploymentStepDto> currentBatch = null;

        foreach (var step in steps)
        {
            if (currentBatch != null &&
                string.Equals(step.StartTrigger, "StartWithPrevious", StringComparison.OrdinalIgnoreCase))
            {
                currentBatch.Add(step);
            }
            else
            {
                currentBatch = new List<DeploymentStepDto> { step };
                batches.Add(currentBatch);
            }
        }

        return batches;
    }
}
