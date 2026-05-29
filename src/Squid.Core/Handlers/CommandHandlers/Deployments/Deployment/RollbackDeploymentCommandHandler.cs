using Squid.Core.Services.Deployments.Rollback;
using Squid.Message.Commands.Deployments.Deployment;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Deployment;

public class RollbackDeploymentCommandHandler : ICommandHandler<RollbackDeploymentCommand, RollbackDeploymentResponse>
{
    private readonly IRollbackService _rollbackService;

    public RollbackDeploymentCommandHandler(IRollbackService rollbackService)
    {
        _rollbackService = rollbackService;
    }

    public async Task<RollbackDeploymentResponse> Handle(IReceiveContext<RollbackDeploymentCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _rollbackService.RollbackDeploymentAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new RollbackDeploymentResponse
        {
            Data = new CreateDeploymentResponseData
            {
                Deployment = @event.Deployment,
                TaskId = @event.TaskId
            }
        };
    }
}
