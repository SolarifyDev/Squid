using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process.Step;

public class DeleteDeploymentStepCommandHandler : ICommandHandler<DeleteDeploymentStepCommand, DeleteDeploymentStepResponse>
{
    private readonly IDeploymentStepService _stepService;

    public DeleteDeploymentStepCommandHandler(IDeploymentStepService stepService)
    {
        _stepService = stepService;
    }

    public async Task<DeleteDeploymentStepResponse> Handle(IReceiveContext<DeleteDeploymentStepCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _stepService.DeleteDeploymentStepsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteDeploymentStepResponse { Data = @event.Data };
    }
}

