using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process.Step;

public class CreateDeploymentStepCommandHandler : ICommandHandler<CreateDeploymentStepCommand, CreateDeploymentStepResponse>
{
    private readonly IDeploymentStepService _stepService;

    public CreateDeploymentStepCommandHandler(IDeploymentStepService stepService)
    {
        _stepService = stepService;
    }

    public async Task<CreateDeploymentStepResponse> Handle(IReceiveContext<CreateDeploymentStepCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _stepService.CreateDeploymentStepAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateDeploymentStepResponse { Data = @event.Data };
    }
}

