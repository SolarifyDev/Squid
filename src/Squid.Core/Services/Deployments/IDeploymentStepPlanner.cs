using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentStepPlanner : IScopedDependency
{
    Task<List<DeploymentStepDto>> PlanStepsAsync(int deploymentId);
}
