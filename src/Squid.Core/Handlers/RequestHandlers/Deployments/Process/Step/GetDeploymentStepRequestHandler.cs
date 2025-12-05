using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Requests.Deployments.Process.Step;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Process.Step;

public class GetDeploymentStepRequestHandler : IRequestHandler<GetDeploymentStepRequest, GetDeploymentStepResponse>
{
    private readonly IDeploymentStepService _stepService;

    public GetDeploymentStepRequestHandler(IDeploymentStepService stepService)
    {
        _stepService = stepService;
    }

    public async Task<GetDeploymentStepResponse> Handle(IReceiveContext<GetDeploymentStepRequest> context, CancellationToken cancellationToken)
    {
        return await _stepService.GetDeploymentStepByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);
    }
}

