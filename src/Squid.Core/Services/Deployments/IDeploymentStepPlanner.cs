using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentStepPlanner
{
    Task<List<DeploymentStepDto>> PlanStepsAsync(int deploymentId);
}
