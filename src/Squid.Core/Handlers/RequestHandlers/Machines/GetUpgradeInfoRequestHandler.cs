using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

/// <summary>
/// Mediator bridge for the FE's per-row upgrade-available probe. Thin —
/// all logic lives in <see cref="IMachineUpgradeService.GetUpgradeInfoAsync"/>
/// so a future bulk-info endpoint can reuse the same decision engine.
/// </summary>
public sealed class GetUpgradeInfoRequestHandler : IRequestHandler<GetUpgradeInfoRequest, GetUpgradeInfoResponse>
{
    private readonly IMachineUpgradeService _upgradeService;

    public GetUpgradeInfoRequestHandler(IMachineUpgradeService upgradeService)
    {
        _upgradeService = upgradeService;
    }

    public async Task<GetUpgradeInfoResponse> Handle(IReceiveContext<GetUpgradeInfoRequest> context, CancellationToken cancellationToken)
    {
        var data = await _upgradeService.GetUpgradeInfoAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetUpgradeInfoResponse { Data = data };
    }
}
