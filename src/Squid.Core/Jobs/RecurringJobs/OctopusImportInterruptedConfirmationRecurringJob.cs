using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Jobs.RecurringJobs;

public class OctopusImportInterruptedConfirmationRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "octopus-import-interrupted-confirmation-recovery";

    public string CronExpression => "* * * * *";

    public async Task Execute() => await mediator.SendAsync<RecoverInterruptedOctopusImportConfirmationsCommand, RecoverInterruptedOctopusImportConfirmationsResponse>(new RecoverInterruptedOctopusImportConfirmationsCommand()).ConfigureAwait(false);
}
