using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Message.Requests.Deployments.Deployment;

public class PreviewDeploymentRequest : IRequest
{
    public DeploymentRequestPayload DeploymentRequestPayload { get; set; } = new();
}

public class PreviewDeploymentResponse : Models.Deployments.Deployment.DeploymentPreviewResult, IResponse;
