using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;

public class ResumeServerTaskCommandHandler(IServerTaskControlService controlService) : ICommandHandler<ResumeServerTaskCommand, ResumeServerTaskResponse>
{
    public async Task<ResumeServerTaskResponse> Handle(IReceiveContext<ResumeServerTaskCommand> context, CancellationToken cancellationToken)
    {
        await controlService.ResumeTaskAsync(context.Message.TaskId, cancellationToken).ConfigureAwait(false);

        return new ResumeServerTaskResponse();
    }
}
