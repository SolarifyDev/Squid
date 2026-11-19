using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class RecoverInterruptedOctopusImportConfirmationsCommandHandler(IOctopusImportConfirmationRecoveryService recoveryService) : ICommandHandler<RecoverInterruptedOctopusImportConfirmationsCommand, RecoverInterruptedOctopusImportConfirmationsResponse>
{
    public async Task<RecoverInterruptedOctopusImportConfirmationsResponse> Handle(IReceiveContext<RecoverInterruptedOctopusImportConfirmationsCommand> context, CancellationToken cancellationToken)
    {
        var recovered = await recoveryService.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);

        return new RecoverInterruptedOctopusImportConfirmationsResponse { Data = new RecoverInterruptedOctopusImportConfirmationsResponseData { Recovered = recovered } };
    }
}
