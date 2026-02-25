using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Core.Jobs.RecurringJobs;

public class RetentionPolicyRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "retention-policy-enforcement";

    public string CronExpression => "0 2 * * *"; // daily at 2 AM

    public async Task Execute()
    {
        await mediator.SendAsync<EnforceRetentionPolicyCommand, EnforceRetentionPolicyResponse>(new EnforceRetentionPolicyCommand()).ConfigureAwait(false);
    }
}
