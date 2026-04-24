namespace Squid.Message.Hardening;

/// <summary>
/// Three-mode policy for security / correctness checks where the strict-by-default
/// rollout would break existing deployments. See <c>~/.claude/CLAUDE.md</c>
/// §"Hardening Three-Mode Enforcement" for the rationale and full pattern.
///
/// <para>Default for every check is <see cref="Warn"/> — preserves backward
/// compatibility, surfaces the insecure config in logs, gives operators a path
/// to remediate before a future major release flips the default to
/// <see cref="Strict"/>.</para>
///
/// <para><see cref="Off"/> is for dev / test / explicit operator opt-out — silent
/// allow, no warning. Use sparingly; the warning in <see cref="Warn"/> mode is
/// the audit trail.</para>
/// </summary>
public enum EnforcementMode
{
    /// <summary>Silent allow. Skip the validator entirely. Dev / tests only.</summary>
    Off,

    /// <summary>Allow + log a structured Serilog warning naming the insecure value
    /// and the env var to switch to <see cref="Strict"/>. Default mode — preserves
    /// backward compat without hiding the tech debt.</summary>
    Warn,

    /// <summary>Reject (throw) with an actionable error message. Operator opts in
    /// for production hardening. Will become the default in a future major release
    /// once deployments have caught up.</summary>
    Strict
}
