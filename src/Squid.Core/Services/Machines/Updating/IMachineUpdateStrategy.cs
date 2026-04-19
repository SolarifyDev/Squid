using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// Per-<c>CommunicationStyle</c> endpoint-update strategy. One implementation
/// per machine type; resolved by <c>MachineService</c> via
/// <c>FirstOrDefault(s =&gt; s.CanHandle(style))</c> — symmetric with
/// <c>IMachineUpgradeStrategy</c>, <c>IEndpointVariableContributor</c>,
/// <c>IHealthCheckStrategy</c>.
///
/// <para><b>Round-6 R6-F</b>: the previous single-dispatch
/// <c>ApplyEndpointUpdate</c> treated all non-OpenClaw styles as
/// KubernetesApi, silently corrupting endpoint JSON for Tentacle/Ssh/K8sAgent
/// machines. Per-style strategies close that architectural hole: each
/// strategy owns ONLY its style's fields, refuses cross-style contamination
/// via <see cref="ValidateForStyle"/>, and deserialises to its own
/// endpoint DTO rather than a shared shape.</para>
///
/// <para>Lifecycle contract: callers MUST invoke <see cref="ValidateForStyle"/>
/// <b>before</b> <see cref="ApplyEndpointUpdate"/>. Validation throws
/// <see cref="Exceptions.MachineEndpointUpdateNotApplicableException"/>
/// without mutating the machine, so EF change-tracking stays clean and no
/// explicit transaction rollback is needed.</para>
/// </summary>
public interface IMachineUpdateStrategy : IScopedDependency
{
    bool CanHandle(string communicationStyle);

    /// <summary>
    /// Throws <see cref="Exceptions.MachineEndpointUpdateNotApplicableException"/>
    /// if <paramref name="command"/> contains any field that doesn't belong
    /// to this style (e.g. <c>ClusterUrl</c> sent to a TentaclePolling
    /// strategy). Must be called before <see cref="ApplyEndpointUpdate"/>.
    /// </summary>
    void ValidateForStyle(int machineId, UpdateMachineCommand command);

    /// <summary>
    /// Mutates <paramref name="machine"/>.Endpoint in place with the
    /// owned fields from <paramref name="command"/>. No-op if none of this
    /// style's fields are set. Returns <see langword="true"/> iff any
    /// endpoint-level mutation actually happened (caller may skip
    /// <c>SaveChanges</c> if nothing changed).
    /// </summary>
    bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand command);
}
