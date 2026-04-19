using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Per-target-type strategy for performing an in-place self-upgrade of a
/// running agent. One implementation per <c>CommunicationStyle</c>; resolved
/// by <c>MachineUpgradeService</c> via <c>FirstOrDefault(s =&gt; s.CanHandle(style))</c>
/// — same shape as <c>IExecutionStrategy</c> and <c>IHealthCheckStrategy</c>.
///
/// <para>
/// Strategies are stateless and idempotent: re-issuing the same upgrade for
/// the same machine + version must be a no-op. Side-effect coordination
/// (cooldown, lock files) lives inside the per-target script the strategy
/// dispatches.
/// </para>
/// </summary>
public interface IMachineUpgradeStrategy : IScopedDependency
{
    bool CanHandle(string communicationStyle);

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
