using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public interface IDeploymentService : IScopedDependency
{
    Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default);
}
