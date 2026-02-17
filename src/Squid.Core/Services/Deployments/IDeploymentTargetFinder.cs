namespace Squid.Core.Services.Deployments;

public interface IDeploymentTargetFinder : IScopedDependency
{
    Task<List<Persistence.Entities.Deployments.Machine>> FindTargetsAsync(Persistence.Entities.Deployments.Deployment deployment, CancellationToken cancellationToken);
}
