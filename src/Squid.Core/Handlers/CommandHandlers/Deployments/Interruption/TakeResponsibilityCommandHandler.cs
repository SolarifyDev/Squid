using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Commands.Deployments.Interruption;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Interruption;

public class TakeResponsibilityCommandHandler : ICommandHandler<TakeResponsibilityCommand, TakeResponsibilityResponse>
{
    private readonly IDeploymentInterruptionService _interruptionService;

    public TakeResponsibilityCommandHandler(IDeploymentInterruptionService interruptionService)
    {
        _interruptionService = interruptionService;
    }

    public async Task<TakeResponsibilityResponse> Handle(IReceiveContext<TakeResponsibilityCommand> context, CancellationToken cancellationToken)
    {
        await _interruptionService.TakeResponsibilityAsync(context.Message.InterruptionId, context.Message.UserId, cancellationToken).ConfigureAwait(false);

        return new TakeResponsibilityResponse();
    }
}
