using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Jobs.RecurringJobs;

public class OctopusImportTemporaryUploadCleanupRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "octopus-import-temporary-upload-cleanup";

    public string CronExpression => "*/30 * * * *";

    public async Task Execute()
    {
        await mediator.SendAsync<EnforceOctopusImportTemporaryUploadCleanupCommand, EnforceOctopusImportTemporaryUploadCleanupResponse>(
            new EnforceOctopusImportTemporaryUploadCleanupCommand()).ConfigureAwait(false);
    }
}
