using Squid.Core.Services.Deployments.Process;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Process;

public class GetDeploymentProcessesRequestHandler : IRequestHandler<GetDeploymentProcessesRequest, GetDeploymentProcessesResponse>
{
    private readonly IDeploymentProcessService _deploymentProcessService;

    public GetDeploymentProcessesRequestHandler(IDeploymentProcessService deploymentProcessService)
    {
        _deploymentProcessService = deploymentProcessService;
    }

    public async Task<GetDeploymentProcessesResponse> Handle(IReceiveContext<GetDeploymentProcessesRequest> context, CancellationToken cancellationToken)
    {
        return await _deploymentProcessService.GetDeploymentProcessesAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
