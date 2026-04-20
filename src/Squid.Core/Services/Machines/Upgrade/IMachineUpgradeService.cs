using Squid.Message.Commands.Machine;
using Squid.Message.Requests.Machines;

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

    /// <summary>
    /// Read-only "can this machine be upgraded right now?" probe for the
    /// frontend's per-row upgrade-available badge. No side effects: does
    /// not acquire the Redis lock, does not dispatch the strategy, does
    /// not invalidate the runtime cache. Pure comparison of cached
    /// current version vs registry latest version, with operator-readable
    /// reason for the decision.
    ///
    /// <para>FE Phase-2 §9.2 (see <c>docs/tentacle-self-upgrade-frontend.md</c>).</para>
    /// </summary>
    Task<GetUpgradeInfoResponseData> GetUpgradeInfoAsync(GetUpgradeInfoRequest request, CancellationToken ct);
}
