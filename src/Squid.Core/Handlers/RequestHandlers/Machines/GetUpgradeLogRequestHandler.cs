using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

/// <summary>
/// Mediator bridge for B4's Phase B log fetch endpoint. Trivial — reads
/// the per-machine cached log text from the timeline store (populated
/// by <see cref="DeploymentExecution.Tentacle.TentacleHealthCheckStrategy"/>
/// on every Capabilities health check) and returns it verbatim.
///
/// <para>No DB hit, no RPC — an in-memory string lookup. Safe for
/// operators to call on-demand.</para>
/// </summary>
public sealed class GetUpgradeLogRequestHandler : IRequestHandler<GetUpgradeLogRequest, GetUpgradeLogResponse>
{
    private readonly IUpgradeEventTimelineStore _timelineStore;

    public GetUpgradeLogRequestHandler(IUpgradeEventTimelineStore timelineStore)
    {
        _timelineStore = timelineStore;
    }

    public Task<GetUpgradeLogResponse> Handle(IReceiveContext<GetUpgradeLogRequest> context, CancellationToken cancellationToken)
    {
        var machineId = context.Message.MachineId;
        var log = _timelineStore.GetLog(machineId);

        var data = new GetUpgradeLogResponseData
        {
            MachineId = machineId,
            Log = log
        };

        return Task.FromResult(new GetUpgradeLogResponse { Data = data });
    }
}
