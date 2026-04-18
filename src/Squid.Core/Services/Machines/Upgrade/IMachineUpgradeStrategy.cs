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
}
