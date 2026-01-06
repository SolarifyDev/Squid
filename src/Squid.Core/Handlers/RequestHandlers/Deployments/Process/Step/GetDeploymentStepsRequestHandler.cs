using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Requests.Deployments.Process.Step;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Process.Step;

public class GetDeploymentStepsRequestHandler : IRequestHandler<GetDeploymentStepsRequest, GetDeploymentStepsResponse>
{
    private readonly IDeploymentStepService _stepService;

    public GetDeploymentStepsRequestHandler(IDeploymentStepService stepService)
    {
        _stepService = stepService;
    }

    public async Task<GetDeploymentStepsResponse> Handle(IReceiveContext<GetDeploymentStepsRequest> context, CancellationToken cancellationToken)
    {
        return await _stepService.GetDeploymentStepsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}

