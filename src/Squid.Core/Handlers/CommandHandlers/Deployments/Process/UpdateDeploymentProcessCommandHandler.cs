using Squid.Core.Services.Deployments.Process;
using Squid.Message.Commands.Deployments.Process;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process;

public class UpdateDeploymentProcessCommandHandler : ICommandHandler<UpdateDeploymentProcessCommand, UpdateDeploymentProcessResponse>
{
    private readonly IDeploymentProcessService _deploymentProcessService;

    public UpdateDeploymentProcessCommandHandler(IDeploymentProcessService deploymentProcessService)
    {
        _deploymentProcessService = deploymentProcessService;
    }

    public async Task<UpdateDeploymentProcessResponse> Handle(IReceiveContext<UpdateDeploymentProcessCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _deploymentProcessService.UpdateDeploymentProcessAsync(context.Message, cancellationToken).ConfigureAwait(false);
        
        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateDeploymentProcessResponse
        {
            Data = new UpdateDeploymentProcessResponseData
            {
                DeploymentProcess = @event.DeploymentProcess
            }
        };
    }
}
