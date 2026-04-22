using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

/// <summary>
/// Read-only fetch of the Phase B upgrade log for a machine (B4, 1.6.0).
/// Returns the full text of <c>/var/log/squid-tentacle-upgrade.log</c>
/// as last captured by a Capabilities health check. Tail-truncated to
/// 50 KB by the agent if the raw log exceeds the cap — "earlier bytes
/// truncated" marker inserted at the head so callers know.
///
/// <para>Use case: operator clicks "View full log" on a failed upgrade
/// task. Today they'd have to SSH to the agent; this endpoint avoids
/// that entirely.</para>
///
/// <para>Empty log when:
/// <list type="bullet">
///   <item>No upgrade has ever run on this machine.</item>
///   <item>Agent is on pre-1.6.0 build (no upgradeLog metadata key).</item>
///   <item>Server pod restarted recently (cache cold; refills on next
///         health check).</item>
///   <item>Agent's /var/log/squid-tentacle-upgrade.log was missing or
///         unreadable at capture time.</item>
/// </list>
/// </para>
/// </summary>
[RequiresPermission(Permission.MachineView)]
public class GetUpgradeLogRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }
}

public class GetUpgradeLogResponse : SquidResponse<GetUpgradeLogResponseData>
{
}

public class GetUpgradeLogResponseData
{
    public int MachineId { get; set; }

    /// <summary>
    /// Raw Phase B log text. Empty string when unavailable.
    /// May be tail-truncated with a "[…N earlier bytes truncated…]"
    /// marker at the head if the raw log exceeded 50 KB.
    /// </summary>
    public string Log { get; set; } = string.Empty;
}
