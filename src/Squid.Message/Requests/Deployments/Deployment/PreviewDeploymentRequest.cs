using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Message.Requests.Deployments.Deployment;

[RequiresPermission(Permission.DeploymentView)]
public class PreviewDeploymentRequest : IRequest
{
    public DeploymentRequestPayload DeploymentRequestPayload { get; set; } = new();
}

public class PreviewDeploymentResponse : DeploymentPreviewResult, IResponse;
