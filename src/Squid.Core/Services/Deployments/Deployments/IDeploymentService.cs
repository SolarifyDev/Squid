using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Core.Services.Deployments.Validation;

namespace Squid.Core.Services.Deployments.Deployments;

public interface IDeploymentService : IScopedDependency
{
    Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default);

    Task<DeploymentEnvironmentValidationResult> ValidateDeploymentEnvironmentAsync(DeploymentValidationContext context, CancellationToken cancellationToken = default);
}
