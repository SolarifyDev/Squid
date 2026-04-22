using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

/// <summary>
/// Mediator bridge for the FE's real-time upgrade progress streaming endpoint
/// (B2/B3, 1.5.x). Reads the per-machine event timeline from the in-memory
/// store populated by <see cref="DeploymentExecution.Tentacle.TentacleHealthCheckStrategy"/>
/// and projects internal <c>UpgradeEvent</c> records onto wire-friendly DTOs
/// so we don't leak internal types into the public Message contract.
///
/// <para>This handler is intentionally trivial — no DB hit, no RPC, just a
/// dictionary lookup + projection. FE can poll this every 2-3s during an
/// active upgrade for sub-second progress updates without stressing the
/// server.</para>
/// </summary>
public sealed class GetUpgradeEventTimelineRequestHandler : IRequestHandler<GetUpgradeEventTimelineRequest, GetUpgradeEventTimelineResponse>
{
    private readonly IUpgradeEventTimelineStore _timelineStore;

    public GetUpgradeEventTimelineRequestHandler(IUpgradeEventTimelineStore timelineStore)
    {
        _timelineStore = timelineStore;
    }

    public Task<GetUpgradeEventTimelineResponse> Handle(IReceiveContext<GetUpgradeEventTimelineRequest> context, CancellationToken cancellationToken)
    {
        var machineId = context.Message.MachineId;
        var events = _timelineStore.Get(machineId);

        var data = new GetUpgradeEventTimelineResponseData
        {
            MachineId = machineId,
            Events = events.Select(e => new UpgradeEventDto
            {
                Timestamp = e.Timestamp,
                Phase = e.Phase,
                Kind = e.Kind,
                Message = e.Message
            }).ToList()
        };

        return Task.FromResult(new GetUpgradeEventTimelineResponse { Data = data });
    }
}
