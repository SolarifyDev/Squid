using Squid.Core.Services.Deployments.Process;
using Squid.Message.Commands.Deployments.Process;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process;

public class CreateDeploymentProcessCommandHandler : ICommandHandler<CreateDeploymentProcessCommand, CreateDeploymentProcessResponse>
{
    private readonly IDeploymentProcessService _deploymentProcessService;

    public CreateDeploymentProcessCommandHandler(IDeploymentProcessService deploymentProcessService)
    {
        _deploymentProcessService = deploymentProcessService;
    }

    public async Task<CreateDeploymentProcessResponse> Handle(IReceiveContext<CreateDeploymentProcessCommand> context, CancellationToken cancellationToken)
    {
        var deploymentProcess = await _deploymentProcessService.CreateDeploymentProcessAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateDeploymentProcessResponse
        {
            Data = new CreateDeploymentProcessResponseData
            {
                DeploymentProcess = deploymentProcess
            }
        };
    }
}
