using Squid.Message.Commands.Deployments.ServerTask;

namespace Squid.Core.Jobs.RecurringJobs;

public class ServerTaskLeaseReaperRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "server-task-lease-reaper";

    public string CronExpression => "* * * * *";

    public async Task Execute() => await mediator.SendAsync<ReapStaleServerTasksCommand, ReapStaleServerTasksResponse>(new ReapStaleServerTasksCommand()).ConfigureAwait(false);
}
