using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;

public class CancelServerTaskCommandHandler(IServerTaskControlService controlService) : ICommandHandler<CancelServerTaskCommand, CancelServerTaskResponse>
{
    public async Task<CancelServerTaskResponse> Handle(IReceiveContext<CancelServerTaskCommand> context, CancellationToken cancellationToken)
    {
        await controlService.CancelTaskAsync(context.Message.TaskId, cancellationToken).ConfigureAwait(false);

        return new CancelServerTaskResponse();
    }
}
