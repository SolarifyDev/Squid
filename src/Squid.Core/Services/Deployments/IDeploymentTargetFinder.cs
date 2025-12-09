namespace Squid.Core.Services.Deployments;

public interface IDeploymentTargetFinder : IScopedDependency
{
    Task<Message.Domain.Deployments.Machine> FindTargetsAsync(Message.Domain.Deployments.Deployment deployment, CancellationToken cancellationToken);
}
