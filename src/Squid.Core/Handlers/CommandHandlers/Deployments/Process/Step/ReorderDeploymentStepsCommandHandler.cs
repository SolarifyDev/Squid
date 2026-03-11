using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process.Step;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Process.Step;

public class ReorderDeploymentStepsCommandHandler : ICommandHandler<ReorderDeploymentStepsCommand, ReorderDeploymentStepsResponse>
{
    private readonly IDeploymentStepService _stepService;

    public ReorderDeploymentStepsCommandHandler(IDeploymentStepService stepService)
    {
        _stepService = stepService;
    }

    public async Task<ReorderDeploymentStepsResponse> Handle(IReceiveContext<ReorderDeploymentStepsCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _stepService.ReorderDeploymentStepsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new ReorderDeploymentStepsResponse { Data = @event.Data };
    }
}
