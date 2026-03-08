using Squid.Core.Services.Machines;

namespace Squid.Core.Jobs.RecurringJobs;

public class MachineHealthCheckRecurringJob(IMachineHealthCheckService healthCheckService) : IRecurringJob
{
    public string JobId => "machine-health-check";

    public string CronExpression => "0 */1 * * *"; // every hour

    public async Task Execute()
    {
        await healthCheckService.RunHealthCheckForAllAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
