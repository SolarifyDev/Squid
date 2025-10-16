using Squid.Core.Services.Deployments.Process;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Process;

public class GetDeploymentProcessRequestHandler : IRequestHandler<GetDeploymentProcessRequest, GetDeploymentProcessResponse>
{
    private readonly IDeploymentProcessService _deploymentProcessService;

    public GetDeploymentProcessRequestHandler(IDeploymentProcessService deploymentProcessService)
    {
        _deploymentProcessService = deploymentProcessService;
    }

    public async Task<GetDeploymentProcessResponse> Handle(IReceiveContext<GetDeploymentProcessRequest> context, CancellationToken cancellationToken)
    {
        var deploymentProcess = await _deploymentProcessService.GetDeploymentProcessByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new GetDeploymentProcessResponse
        {
            Data = new GetDeploymentProcessResponseData
            {
                DeploymentProcess = deploymentProcess
            }
        };
    }
}
