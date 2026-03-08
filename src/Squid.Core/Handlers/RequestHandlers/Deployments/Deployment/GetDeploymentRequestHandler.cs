using Squid.Core.Services.Deployments.Deployments;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;

public class GetDeploymentRequestHandler : IRequestHandler<GetDeploymentRequest, GetDeploymentResponse>
{
    private readonly IDeploymentService _deploymentService;

    public GetDeploymentRequestHandler(IDeploymentService deploymentService)
    {
        _deploymentService = deploymentService;
    }

    public async Task<GetDeploymentResponse> Handle(IReceiveContext<GetDeploymentRequest> context, CancellationToken cancellationToken)
    {
        return await _deploymentService.GetDeploymentByIdAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
