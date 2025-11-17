using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentPlanService
{
    Task<DeploymentPlanDto> GeneratePlanAsync(int deploymentId, CancellationToken cancellationToken);
}
