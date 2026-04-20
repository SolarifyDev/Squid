using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

/// <summary>
/// Read-only probe for the FE's "upgrade available" badge: given a machine,
/// report what version it's currently running, what's the latest published
/// version for its CommunicationStyle, and whether a one-click upgrade is
/// expected to make progress.
///
/// <para>No side effects — no Redis lock, no strategy dispatch. Frontend
/// can call this per-row on the machine list page without fan-out throttling
/// concerns (the underlying registry caches Docker Hub queries for 10
/// minutes with in-flight dedupe, so 50 rows collapse to 1 HTTP round-trip).</para>
///
/// <para>FE Phase-2 §9.2 (see <c>docs/tentacle-self-upgrade-frontend.md</c>).</para>
/// </summary>
[RequiresPermission(Permission.MachineView)]
public class GetUpgradeInfoRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }
}

public class GetUpgradeInfoResponse : SquidResponse<GetUpgradeInfoResponseData>
{
}

public class GetUpgradeInfoResponseData
{
    public int MachineId { get; set; }

    /// <summary>
    /// Agent's running binary version from the runtime-capabilities cache.
    /// Empty when the agent has never successfully health-checked yet.
    /// </summary>
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    /// Latest published version for this machine's CommunicationStyle as
    /// resolved by <c>ITentacleVersionRegistry</c> (env override → fresh
    /// cache → live Docker Hub → stale cache). Empty when Docker Hub is
    /// unreachable AND no cache AND no env override.
    /// </summary>
    public string LatestAvailableVersion { get; set; } = string.Empty;

    /// <summary>
    /// Whether the FE should surface an "Upgrade" action for this machine.
    /// <see langword="true"/> ⇒ safe to render the badge / button.
    /// <see langword="false"/> ⇒ hide or disable the upgrade affordance;
    /// <see cref="Reason"/> contains the human-readable explanation.
    /// </summary>
    public bool CanUpgrade { get; set; }

    /// <summary>
    /// Operator-facing one-liner explaining the <see cref="CanUpgrade"/>
    /// decision. Safe to render directly in a tooltip without sanitisation.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
