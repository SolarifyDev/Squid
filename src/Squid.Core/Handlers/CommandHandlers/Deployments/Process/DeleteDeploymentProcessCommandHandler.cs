using Squid.Core.Services.Deployments.Process;
using Squid.Message.Commands.Deployments.Process;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process;

public class DeleteDeploymentProcessCommandHandler : ICommandHandler<DeleteDeploymentProcessCommand, DeleteDeploymentProcessResponse>
{
    private readonly IDeploymentProcessService _deploymentProcessService;

    public DeleteDeploymentProcessCommandHandler(IDeploymentProcessService deploymentProcessService)
    {
        _deploymentProcessService = deploymentProcessService;
    }

    public async Task<DeleteDeploymentProcessResponse> Handle(IReceiveContext<DeleteDeploymentProcessCommand> context, CancellationToken cancellationToken)
    {
        await _deploymentProcessService.DeleteDeploymentProcessAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new DeleteDeploymentProcessResponse
        {
            Data = new DeleteDeploymentProcessResponseData
            {
                Message = "DeploymentProcess deleted successfully"
            }
        };
    }
}
