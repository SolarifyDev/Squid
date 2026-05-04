using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

/// <summary>
/// P1-Phase12.E.8 — mediator bridge for the agent-reported upgrade-status
/// snapshot. Reads the per-machine cached <see cref="UpgradeStatusPayload"/>
/// from the timeline store (populated by
/// <see cref="DeploymentExecution.Tentacle.TentacleHealthCheckStrategy"/>
/// on every Capabilities health check) and projects it onto the
/// wire-friendly <see cref="UpgradeStatusDto"/>.
///
/// <para><b>Exposed contract — the structured <c>ExitCode</c> field</b>:
/// the WHOLE reason this endpoint exists separately from
/// <c>GetUpgradeEventTimelineRequest</c> is to surface the structured
/// integer exit code that ONLY appears in <c>last-upgrade.json</c> (NOT
/// in the JSONL events stream). Phase 12.E.7.B-2 added the parser
/// support; this handler completes the chain to the operator.</para>
///
/// <para>No DB hit, no RPC — an in-memory dictionary lookup + projection.
/// Safe for the FE to poll on demand; no rate-limiting concerns.</para>
/// </summary>
public sealed class GetUpgradeStatusRequestHandler : IRequestHandler<GetUpgradeStatusRequest, GetUpgradeStatusResponse>
{
    private readonly IUpgradeEventTimelineStore _timelineStore;

    public GetUpgradeStatusRequestHandler(IUpgradeEventTimelineStore timelineStore)
    {
        _timelineStore = timelineStore;
    }

    public Task<GetUpgradeStatusResponse> Handle(IReceiveContext<GetUpgradeStatusRequest> context, CancellationToken cancellationToken)
    {
        var machineId = context.Message.MachineId;
        var payload = _timelineStore.GetStatus(machineId);

        var data = new GetUpgradeStatusResponseData
        {
            MachineId = machineId,
            Status = payload == null ? null : Project(payload)
        };

        return Task.FromResult(new GetUpgradeStatusResponse { Data = data });
    }

    /// <summary>
    /// Internal-type → wire-DTO projection. Field names + types mirror the
    /// agent's JSON shape; nullable handling preserves the
    /// "didn't write the field" vs "wrote the field with the default value"
    /// distinction (esp. <see cref="UpgradeStatusDto.ExitCode"/> = 0 vs null).
    /// </summary>
    internal static UpgradeStatusDto Project(UpgradeStatusPayload payload) => new()
    {
        SchemaVersion = payload.SchemaVersion,
        Status = payload.Status,
        TargetVersion = payload.TargetVersion,
        InstallMethod = payload.InstallMethod,
        Detail = payload.Detail,
        ExitCode = payload.ExitCode,
        StartedAt = payload.StartedAt,
        UpdatedAt = payload.UpdatedAt,
        ScriptPid = payload.ScriptPid
    };
}
