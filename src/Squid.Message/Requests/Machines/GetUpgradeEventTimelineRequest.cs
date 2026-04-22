using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

/// <summary>
/// Read-only probe for the in-flight upgrade progress timeline. Given a
/// machine, returns the agent-reported list of structured events emitted
/// during the most recent upgrade attempt: <c>start</c>, <c>method-selected</c>,
/// <c>scope-exec</c>, <c>restart-start</c>, <c>healthz-pass</c>, <c>success</c>
/// (and failure-path events).
///
/// <para>FE polls this endpoint every 2-3s during an active upgrade to
/// stream a live progress narrative into the task activity log — the
/// gap between "click Upgrade" and "binary swap complete" goes from
/// "20 seconds of dead silence" to "real-time per-step transitions."</para>
///
/// <para>No side effects — no DB write, no RPC. Reads from the
/// process-local in-memory cache populated by every health check
/// (<see cref="Squid.Message.Models.Deployments.Machine"/> probe). FE
/// can poll aggressively without throttling concerns.</para>
///
/// <para>Empty events list when:
/// <list type="bullet">
///   <item>No upgrade has ever been attempted on this machine.</item>
///   <item>Agent is on 1.4.x (no events file emitted, no metadata key).</item>
///   <item>Server pod restarted recently (cache cold; refills on next health probe).</item>
/// </list>
/// </para>
/// </summary>
[RequiresPermission(Permission.MachineView)]
public class GetUpgradeEventTimelineRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }
}

public class GetUpgradeEventTimelineResponse : SquidResponse<GetUpgradeEventTimelineResponseData>
{
}

public class GetUpgradeEventTimelineResponseData
{
    public int MachineId { get; set; }

    /// <summary>
    /// Chronological list of upgrade events from the most recent attempt.
    /// Empty when no attempt has been made or the cache is cold. Each
    /// event includes timestamp, phase ("A" pre-scope, "B" in scope),
    /// kind tag, and human-readable message.
    /// </summary>
    public List<UpgradeEventDto> Events { get; set; } = new();
}

/// <summary>
/// Wire-friendly projection of <c>UpgradeEvent</c>. Avoids leaking the
/// internal type from Squid.Core into the public Message contract.
/// </summary>
public class UpgradeEventDto
{
    public DateTimeOffset? Timestamp { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
