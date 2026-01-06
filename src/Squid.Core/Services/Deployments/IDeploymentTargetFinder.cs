namespace Squid.Core.Services.Deployments;

public interface IDeploymentTargetFinder : IScopedDependency
{
    Task<Persistence.Data.Domain.Deployments.Machine> FindTargetsAsync(Persistence.Data.Domain.Deployments.Deployment deployment, CancellationToken cancellationToken);
}
