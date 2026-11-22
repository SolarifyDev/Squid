using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;

public class ReapStaleServerTasksCommandHandler(IServerTaskLeaseReaperService reaperService) : ICommandHandler<ReapStaleServerTasksCommand, ReapStaleServerTasksResponse>
{
    public async Task<ReapStaleServerTasksResponse> Handle(IReceiveContext<ReapStaleServerTasksCommand> context, CancellationToken cancellationToken)
    {
        var recovered = await reaperService.ReapStaleAsync(cancellationToken).ConfigureAwait(false);

        return new ReapStaleServerTasksResponse { Data = new ReapStaleServerTasksResponseData { Recovered = recovered } };
    }
}
