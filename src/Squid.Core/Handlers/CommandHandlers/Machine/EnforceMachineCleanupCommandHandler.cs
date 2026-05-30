using Squid.Core.Services.Machines.Cleanup;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

public class EnforceMachineCleanupCommandHandler(IMachineCleanupService machineCleanupService)
    : ICommandHandler<EnforceMachineCleanupCommand, EnforceMachineCleanupResponse>
{
    public async Task<EnforceMachineCleanupResponse> Handle(IReceiveContext<EnforceMachineCleanupCommand> context, CancellationToken cancellationToken)
    {
        var outcome = await machineCleanupService.EnforceCleanupAsync(cancellationToken).ConfigureAwait(false);

        return new EnforceMachineCleanupResponse
        {
            Data = new EnforceMachineCleanupResponseData
            {
                Scanned = outcome.Scanned,
                Eligible = outcome.Eligible,
                Deleted = outcome.Deleted
            }
        };
    }
}
