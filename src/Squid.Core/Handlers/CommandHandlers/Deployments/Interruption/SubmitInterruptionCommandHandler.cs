using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Commands.Deployments.Interruption;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Interruption;

public class SubmitInterruptionCommandHandler : ICommandHandler<SubmitInterruptionCommand, SubmitInterruptionResponse>
{
    private readonly IDeploymentInterruptionService _interruptionService;

    public SubmitInterruptionCommandHandler(IDeploymentInterruptionService interruptionService)
    {
        _interruptionService = interruptionService;
    }

    public async Task<SubmitInterruptionResponse> Handle(IReceiveContext<SubmitInterruptionCommand> context, CancellationToken cancellationToken)
    {
        await _interruptionService.SubmitInterruptionAsync(context.Message.InterruptionId, context.Message.Values, cancellationToken).ConfigureAwait(false);

        return new SubmitInterruptionResponse();
    }
}
