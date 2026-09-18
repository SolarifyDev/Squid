using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class EnforceOctopusImportTemporaryUploadCleanupCommandHandler(
    IOctopusImportTemporaryUploadCleanupService cleanupService)
    : ICommandHandler<EnforceOctopusImportTemporaryUploadCleanupCommand, EnforceOctopusImportTemporaryUploadCleanupResponse>
{
    public async Task<EnforceOctopusImportTemporaryUploadCleanupResponse> Handle(
        IReceiveContext<EnforceOctopusImportTemporaryUploadCleanupCommand> context,
        CancellationToken cancellationToken)
    {
        var outcome = await cleanupService.EnforceCleanupAsync(cancellationToken).ConfigureAwait(false);

        return new EnforceOctopusImportTemporaryUploadCleanupResponse
        {
            Data = new EnforceOctopusImportTemporaryUploadCleanupResponseData
            {
                ExpiredSessions = outcome.ExpiredSessions,
                InterruptedImportsFailed = outcome.InterruptedImportsFailed,
                Scanned = outcome.Scanned,
                Cleaned = outcome.Cleaned,
                Failed = outcome.Failed
            }
        };
    }
}
