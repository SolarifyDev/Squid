using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Per-target-type strategy for performing an in-place self-upgrade of a
/// running agent. One implementation per <c>CommunicationStyle</c> + agent OS;
/// resolved by <c>MachineUpgradeService</c> via
/// <c>FirstOrDefault(s =&gt; s.CanHandle(style, capabilities))</c> — same shape
/// as <c>IExecutionStrategy</c> and <c>IHealthCheckStrategy</c>.
///
/// <para>
/// Strategies are stateless and idempotent: re-issuing the same upgrade for
/// the same machine + version must be a no-op. Side-effect coordination
/// (cooldown, lock files) lives inside the per-target script the strategy
/// dispatches.
/// </para>
///
/// <para>
/// <b> widening:</b> <see cref="CanHandle"/> takes
/// <see cref="MachineRuntimeCapabilities"/> in addition to the
/// communication-style string. This is required because Windows and Linux
/// tentacles use the SAME wire-protocol communication style values
/// (<c>TentaclePolling</c> / <c>TentacleListening</c>) — Halibut doesn't
/// distinguish them. The agent OS, reported via the health-check
/// capabilities probe and cached in
/// <see cref="IMachineRuntimeCapabilitiesCache"/>, is what differentiates
/// <c>LinuxTentacleUpgradeStrategy</c> from
/// <c>WindowsTentacleUpgradeStrategy</c>. Cold cache (no health check yet)
/// → empty <see cref="MachineRuntimeCapabilities.Os"/>; the Linux strategy
/// claims that case as the historical default to preserve behaviour.
/// </para>
/// </summary>
public interface IMachineUpgradeStrategy : IScopedDependency
{
    /// <summary>
    /// Returns true when this strategy is the right one to dispatch an upgrade
    /// against an agent with the given communication style + cached capabilities.
    /// </summary>
    /// <remarks>
    /// <para>The resolver enforces an "exactly one owner" invariant — if two
    /// strategies return true for the same (style, capabilities) pair, the
    /// resolver throws with both class names so the drift is visible at the
    /// first dispatch attempt rather than producing a silent wrong-binary
    /// install.</para>
    ///
    /// <para>Implementations should be CHEAP — the resolver may call this
    /// on every registered strategy per upgrade dispatch. No IO, no async
    /// work; just a string + property check.</para>
    /// </remarks>
    bool CanHandle(string communicationStyle, MachineRuntimeCapabilities capabilities);

    Task<MachineUpgradeOutcome> UpgradeAsync(
        Machine machine,
        string targetVersion,
        CancellationToken ct);
}

/// <summary>
/// Server-side outcome of a single upgrade attempt. Distinct from the
/// transport DTO <c>UpgradeMachineResponseData</c> — this lives in Core and
/// stays free of HTTP / serialization concerns. The mediator handler maps
/// this to the response DTO.
/// </summary>
public sealed class MachineUpgradeOutcome
{
    public required Squid.Message.Commands.Machine.MachineUpgradeStatus Status { get; init; }

    public required string Detail { get; init; }

    /// <summary>
    /// Whether the agent's running binary may now differ from what the
    /// runtime-capabilities cache reports. <see langword="true"/> ⇒ the
    /// orchestrator drops the cache so the next health check fetches a
    /// fresh version reading.
    ///
    /// <para>Outcome-driven instead of <c>switch</c>-on-<see cref="Squid.Message.Commands.Machine.MachineUpgradeStatus"/>
    /// (audit N-6) so adding a new status value is a deliberate per-strategy
    /// decision — not a silent miss caused by an
    /// <c>is Upgraded or Initiated</c> check that didn't get updated.</para>
    ///
    /// <para><b>required</b> (Round-3 audit A4): a new <see cref="MachineUpgradeOutcome"/>
    /// construction site MUST specify this explicitly. Without required, a
    /// strategy author can forget to set it and silently inherit the default
    /// (<see langword="false"/>), which for a genuine successful upgrade
    /// would leave the UI showing the old version until the next health
    /// check — a subtle "upgrade didn't take" impression.</para>
    /// </summary>
    public required bool AgentVersionMayHaveChanged { get; init; }
}
