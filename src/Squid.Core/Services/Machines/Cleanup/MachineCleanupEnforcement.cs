using Squid.Message.Hardening;

namespace Squid.Core.Services.Machines.Cleanup;

/// <summary>
/// Three-mode enforcement (global CLAUDE.md Rule 11) for the machine-policy
/// "Clean up — delete unavailable deployment targets after N" behaviour. Deleting
/// a deployment target is destructive, so the global default is a DRY RUN: even
/// when an operator opts a machine policy into <c>DeleteUnavailableMachines</c>,
/// nothing is deleted until they ALSO flip this switch to <c>strict</c>.
///
/// <list type="bullet">
///   <item><b>off</b> — never delete; skip the sweep entirely (debug-level only).</item>
///   <item><b>warn</b> (DEFAULT) — evaluate eligibility and emit a structured
///         warning naming each machine that WOULD be deleted + how to switch to
///         strict, but delete nothing. Non-breaking: today's behaviour is "never
///         auto-delete", which this preserves while surfacing the intent.</item>
///   <item><b>strict</b> — actually delete the eligible machines via the same
///         safe delete path the operator-facing "Delete" action uses.</item>
/// </list>
///
/// <para>The per-policy <c>DeleteMachinesBehavior</c> still gates <i>eligibility</i>
/// (default <c>DoNotDelete</c> → never eligible). This switch only governs whether
/// an eligible machine is logged (warn) or actually removed (strict). Both gates
/// must opt in for a deletion to occur — a deliberate double-opt-in for a
/// destructive operation.</para>
/// </summary>
public static class MachineCleanupEnforcement
{
    /// <summary>Operator escape hatch (Rule 8). Pinned by a unit test.</summary>
    public const string EnvVar = "SQUID_MACHINE_CLEANUP_ENFORCEMENT";

    /// <summary>Resolve the current mode from <see cref="EnvVar"/>; defaults to
    /// <see cref="EnforcementMode.Warn"/> (dry-run) when unset/blank/unrecognised.</summary>
    public static EnforcementMode ResolveMode() => EnforcementModeReader.Read(EnvVar);
}
