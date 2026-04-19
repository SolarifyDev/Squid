using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Handlers.CommandHandlers.Machine;

/// <summary>
/// Mediator entry point for the per-machine upgrade endpoint. Thin —
/// orchestration lives in <see cref="IMachineUpgradeService"/> so the same
/// flow can later be invoked from a bulk-upgrade task scheduler without
/// going through HTTP.
/// </summary>
public sealed class UpgradeMachineCommandHandler : ICommandHandler<UpgradeMachineCommand, UpgradeMachineResponse>
{
    private readonly IMachineUpgradeService _upgradeService;

    public UpgradeMachineCommandHandler(IMachineUpgradeService upgradeService)
    {
        _upgradeService = upgradeService;
    }

    public async Task<UpgradeMachineResponse> Handle(IReceiveContext<UpgradeMachineCommand> context, CancellationToken cancellationToken)
    {
        var data = await _upgradeService.UpgradeAsync(context.Message, cancellationToken).ConfigureAwait(false);
        return new UpgradeMachineResponse { Data = data };
    }
}
