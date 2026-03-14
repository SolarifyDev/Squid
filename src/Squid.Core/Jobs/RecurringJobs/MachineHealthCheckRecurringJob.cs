using Squid.Message.Commands.Machine;

namespace Squid.Core.Jobs.RecurringJobs;

public class MachineHealthCheckRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "machine-health-check";

    public string CronExpression => "*/1 * * * *"; // every minute

    public async Task Execute()
    {
        await mediator.SendAsync<AutoMachineHealthCheckCommand, AutoMachineHealthCheckResponse>(new AutoMachineHealthCheckCommand()).ConfigureAwait(false);
    }
}
