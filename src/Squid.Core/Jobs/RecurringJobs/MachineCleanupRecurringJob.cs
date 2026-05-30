using Squid.Message.Commands.Machine;

namespace Squid.Core.Jobs.RecurringJobs;

public class MachineCleanupRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "machine-cleanup-enforcement";

    public string CronExpression => "0 3 * * *"; // daily at 3 AM

    public async Task Execute()
    {
        await mediator.SendAsync<EnforceMachineCleanupCommand, EnforceMachineCleanupResponse>(new EnforceMachineCleanupCommand()).ConfigureAwait(false);
    }
}
