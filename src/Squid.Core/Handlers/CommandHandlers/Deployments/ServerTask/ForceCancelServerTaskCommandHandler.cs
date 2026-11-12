using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;

public class ForceCancelServerTaskCommandHandler(IServerTaskControlService controlService) : ICommandHandler<ForceCancelServerTaskCommand, ForceCancelServerTaskResponse>
{
    public async Task<ForceCancelServerTaskResponse> Handle(IReceiveContext<ForceCancelServerTaskCommand> context, CancellationToken cancellationToken)
    {
        await controlService.ForceCancelStuckTaskAsync(context.Message.TaskId, cancellationToken).ConfigureAwait(false);

        return new ForceCancelServerTaskResponse();
    }
}
