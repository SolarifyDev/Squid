using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.Interruption;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Interruption;

public class SubmitInterruptionCommandHandler(
    IDeploymentInterruptionService interruptionService,
    IServerTaskControlService controlService) : ICommandHandler<SubmitInterruptionCommand, SubmitInterruptionResponse>
{
    public async Task<SubmitInterruptionResponse> Handle(IReceiveContext<SubmitInterruptionCommand> context, CancellationToken cancellationToken)
    {
        await interruptionService.SubmitInterruptionAsync(context.Message.InterruptionId, context.Message.Values, cancellationToken).ConfigureAwait(false);

        var interruption = await interruptionService.GetInterruptionByIdAsync(context.Message.InterruptionId, cancellationToken).ConfigureAwait(false);

        if (interruption != null)
            await controlService.TryAutoResumeAsync(interruption.ServerTaskId, cancellationToken).ConfigureAwait(false);

        return new SubmitInterruptionResponse();
    }
}
