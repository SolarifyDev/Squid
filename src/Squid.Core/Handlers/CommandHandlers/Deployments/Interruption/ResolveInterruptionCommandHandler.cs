using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Commands.Deployments.Interruption;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Interruption;

public class ResolveInterruptionCommandHandler : ICommandHandler<ResolveInterruptionCommand, ResolveInterruptionResponse>
{
    private readonly IDeploymentInterruptionService _interruptionService;

    public ResolveInterruptionCommandHandler(IDeploymentInterruptionService interruptionService)
    {
        _interruptionService = interruptionService;
    }

    public async Task<ResolveInterruptionResponse> Handle(IReceiveContext<ResolveInterruptionCommand> context, CancellationToken cancellationToken)
    {
        await _interruptionService.ResolveInterruptionAsync(context.Message.InterruptionId, context.Message.Action, cancellationToken).ConfigureAwait(false);

        return new ResolveInterruptionResponse();
    }
}
