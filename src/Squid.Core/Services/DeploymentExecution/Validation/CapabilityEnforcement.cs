using Squid.Message.Hardening;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Three-mode enforcement (global CLAUDE.md Rule 11) for deployment
/// capability/suitability mismatches — when the planner KNOWS a target cannot
/// run an action (unsupported script syntax, action type, required feature, or
/// OS/role capability slot), based on the target's advertised capabilities.
///
/// <list type="bullet">
///   <item><b>off</b> — skip the incompatible (action × target) silently
///         (debug-level only). The legacy "graceful filter" with no log noise.</item>
///   <item><b>warn</b> (DEFAULT) — skip it AND emit a structured warning naming
///         the missing capability + how to switch to strict. This reproduces
///         today's behaviour exactly, so the default is non-breaking.</item>
///   <item><b>strict</b> — fail the deployment PRE-FLIGHT (before any step runs)
///         when a capability mismatch is detected, so a mis-targeted action is a
///         hard error rather than a silent skip.</item>
/// </list>
///
/// <para><b>Cold cache stays optimistic-allow in every mode</b>: if a target has
/// advertised no capabilities yet (no health check landed), the planner cannot
/// prove incompatibility, so the dispatch is allowed and any real mismatch
/// surfaces at runtime — the same outcome a capability-unaware server gives.
/// Enforcement only ever acts on a KNOWN (warm-cache) mismatch.</para>
///
/// <para>Scope: this toggle governs capability suitability only. Target-selection
/// concerns (no matching targets for a role, unresolved transport) are separate
/// and unaffected.</para>
/// </summary>
public static class CapabilityEnforcement
{
    /// <summary>Operator escape hatch (Rule 8). Pinned by a unit test.</summary>
    public const string EnvVar = "SQUID_CAPABILITY_ENFORCEMENT";

    /// <summary>Resolve the current mode from <see cref="EnvVar"/>; defaults to
    /// <see cref="EnforcementMode.Warn"/> when unset/blank/unrecognised.</summary>
    public static EnforcementMode ResolveMode() => EnforcementModeReader.Read(EnvVar);
}
