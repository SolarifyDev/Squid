using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

/// <summary>
/// Triggers a self-upgrade of the agent installed on the target machine.
/// Asynchronous: the server schedules the upgrade and returns immediately
/// with a result describing what was attempted; the caller polls subsequent
/// health checks (or the machine's <c>AgentVersion</c> column) to confirm
/// the new binary is running.
///
/// <para>
/// Per-target dispatch is via <c>IMachineUpgradeStrategy</c> matched by the
/// machine's <c>CommunicationStyle</c> (LinuxTentacle polling/listening,
/// KubernetesAgent helm upgrade, etc.). Same one-strategy-per-style pattern
/// the deployment pipeline already uses for execution and health checks.
/// </para>
/// </summary>
[RequiresPermission(Permission.MachineEdit)]
public class UpgradeMachineCommand : ICommand, ISpaceScoped
{
    public int MachineId { get; set; }

    /// <summary>
    /// Optional explicit version. When null/blank the server resolves the
    /// latest published Tentacle for this machine's CommunicationStyle via
    /// <c>ITentacleVersionRegistry</c> (env override → fresh cache → live
    /// Docker Hub → stale cache fallback). Decoupled from the server's own
    /// release version so a tentacle hotfix can ship without a server
    /// release.
    /// </summary>
    public string TargetVersion { get; set; }

    /// <summary>
    /// Opt-in escape hatch for emergency downgrades (Round-3 audit A5).
    /// By default (false), a request whose <c>TargetVersion</c> is LOWER
    /// than the agent's current version is refused with
    /// <see cref="MachineUpgradeStatus.AlreadyUpToDate"/> and a detail
    /// that spells out the block. Setting this to true bypasses that
    /// guard — intended for "1.4.2 has a bad regression, revert to
    /// 1.4.0" scenarios where the operator explicitly knows they want
    /// to go back.
    ///
    /// <para>Does NOT bypass the same-version guard: requesting the SAME
    /// version the agent is already on is still a no-op even with this
    /// flag (saves a pointless filesystem + network round-trip).</para>
    /// </summary>
    public bool AllowDowngrade { get; set; }

    /// <summary>
    /// Space scope for the <see cref="Attributes.RequiresPermissionAttribute"/>
    /// check. <b>Security note (audit H-19):</b> the upgrade controller nulls
    /// this out before handing the command to the mediator — the body is not
    /// a trusted source for the space to authorize against. The pipeline
    /// then populates from the <c>X-Space-Id</c> HTTP header.
    ///
    /// <para>Full fix (permission check AFTER resource lookup) is a
    /// framework-level change tracked outside this feature; today the
    /// operator can still set <c>X-Space-Id</c> to a space they're a member
    /// of and target a machine in a DIFFERENT space, so treat the space
    /// boundary as advisory until that framework change lands.</para>
    /// </summary>
    public int? SpaceId { get; set; }
}

public class UpgradeMachineResponse : SquidResponse<UpgradeMachineResponseData>
{
}

public class UpgradeMachineResponseData
{
    public int MachineId { get; set; }

    /// <summary>Machine name for log/UI convenience — saves a round-trip.</summary>
    public string MachineName { get; set; }

    /// <summary>
    /// What the server determined was the current agent version (from the
    /// runtime capabilities cache populated by health checks). May be empty
    /// if the agent has never reported capabilities yet — in which case the
    /// upgrade still attempts but cannot pre-skip "AlreadyUpToDate".
    /// </summary>
    public string CurrentVersion { get; set; }

    public string TargetVersion { get; set; }

    public MachineUpgradeStatus Status { get; set; }

    /// <summary>
    /// Human-readable diagnostic — for failures includes remediation hints
    /// (network, permissions, version not found in bundle); for success
    /// includes the elapsed time + final stdout tail of the upgrade script.
    /// </summary>
    public string Detail { get; set; }
}

/// <summary>
/// Outcome of a single per-machine upgrade attempt. Treats "no-op because
/// already at target" as a success category distinct from "actually upgraded"
/// so the UI can render differently (e.g. "12 of 50 machines were already up
/// to date").
/// </summary>
public enum MachineUpgradeStatus
{
    /// <summary>Agent is now running <c>TargetVersion</c>.</summary>
    Upgraded = 0,

    /// <summary>Agent was already on the requested version; nothing to do.</summary>
    AlreadyUpToDate = 1,

    /// <summary>Communication style isn't supported yet (e.g. SSH targets, custom transports).</summary>
    NotSupported = 2,

    /// <summary>Upgrade attempted but the agent failed to come back up healthy on the new version.</summary>
    Failed = 3,

    /// <summary>Server couldn't determine the current version (cache miss) but proceeded; outcome unknown until the next health check.</summary>
    Initiated = 4
}
