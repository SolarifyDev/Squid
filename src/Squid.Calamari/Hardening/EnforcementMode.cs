namespace Squid.Calamari.Hardening;

/// <summary>
/// Internal copy of the project-wide three-mode enforcement enum — see
/// <c>Squid.Message.Hardening.EnforcementMode</c> (mirror-pinned by test).
/// Squid.Calamari is a standalone executable with no project refs to Squid.Message,
/// so the enum is duplicated here. The values + names MUST match the server copy.
///
/// <para>See <c>~/.claude/CLAUDE.md</c> §"Hardening Three-Mode Enforcement" for
/// the full pattern.</para>
/// </summary>
public enum EnforcementMode
{
    /// <summary>Silent allow.</summary>
    Off,

    /// <summary>Allow + log warning (default).</summary>
    Warn,

    /// <summary>Reject (throw).</summary>
    Strict
}
