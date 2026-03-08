using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public interface IDeploymentService : IScopedDependency
{
    Task<DeploymentCreatedEvent> CreateDeploymentAsync(CreateDeploymentCommand command, CancellationToken cancellationToken = default);

    Task<DeploymentPreviewResult> PreviewDeploymentAsync(DeploymentRequestPayload deploymentRequestPayload, CancellationToken cancellationToken = default);

    Task<GetDeploymentResponse> GetDeploymentByIdAsync(GetDeploymentRequest request, CancellationToken cancellationToken = default);
}
