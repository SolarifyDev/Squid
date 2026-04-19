using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Server-side orchestrator for the "Upgrade Tentacle" feature. Resolves the
/// machine + current/target versions, picks the right
/// <see cref="IMachineUpgradeStrategy"/> by communication style, and returns a
/// <see cref="UpgradeMachineResponseData"/> the controller can hand back.
///
/// <para>Mirrors the pattern of <c>IMachineRegistrationService</c> /
/// <c>IMachineHealthCheckService</c> — one service interface per machine
/// lifecycle operation, dispatch via per-style strategies.</para>
/// </summary>
public interface IMachineUpgradeService : IScopedDependency
{
    Task<UpgradeMachineResponseData> UpgradeAsync(UpgradeMachineCommand command, CancellationToken ct);
}
