using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentStepPlanner : IScopedDependency
{
    Task<List<DeploymentStepDto>> PlanStepsAsync(int deploymentId);
}
