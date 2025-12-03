using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process.Step;

public class UpdateDeploymentStepCommandHandler : ICommandHandler<UpdateDeploymentStepCommand, UpdateDeploymentStepResponse>
{
    private readonly IDeploymentStepService _stepService;

    public UpdateDeploymentStepCommandHandler(IDeploymentStepService stepService)
    {
        _stepService = stepService;
    }

    public async Task<UpdateDeploymentStepResponse> Handle(IReceiveContext<UpdateDeploymentStepCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _stepService.UpdateDeploymentStepAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateDeploymentStepResponse { Data = @event.Data };
    }
}

