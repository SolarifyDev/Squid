namespace Squid.Message.Hardening;

/// <summary>
/// Parses <see cref="EnforcementMode"/> from an environment variable. One single
/// helper used by every hardening check across the codebase so the parsing
/// vocabulary stays identical (no validator-specific spellings, no aliases that
/// drift over time).
///
/// <para>Recognised values (case-insensitive, trimmed):</para>
/// <list type="bullet">
///   <item><c>off</c> / <c>disabled</c> / <c>0</c> / <c>false</c> → <see cref="EnforcementMode.Off"/></item>
///   <item><c>warn</c> / <c>warning</c> → <see cref="EnforcementMode.Warn"/></item>
///   <item><c>strict</c> / <c>enforce</c> / <c>1</c> / <c>true</c> → <see cref="EnforcementMode.Strict"/></item>
///   <item>(unset / blank / unrecognised) → <paramref name="defaultMode"/>
///         (typically <see cref="EnforcementMode.Warn"/>)</item>
/// </list>
///
/// <para>Unrecognised values fall back to default rather than throwing — a typo'd
/// env var must never crash the process. The returned mode is always one of the
/// three enum values; downstream code can switch exhaustively.</para>
/// </summary>
public static class EnforcementModeReader
{
    /// <summary>
    /// Resolve enforcement mode from the named environment variable, with
    /// <paramref name="defaultMode"/> as the fallback when the env var is unset,
    /// blank, or unrecognised.
    /// </summary>
    public static EnforcementMode Read(string envVarName, EnforcementMode defaultMode = EnforcementMode.Warn)
    {
        var raw = System.Environment.GetEnvironmentVariable(envVarName);

        if (string.IsNullOrWhiteSpace(raw)) return defaultMode;

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" or "disabled" or "0" or "false" => EnforcementMode.Off,
            "warn" or "warning" => EnforcementMode.Warn,
            "strict" or "enforce" or "1" or "true" => EnforcementMode.Strict,
            _ => defaultMode
        };
    }
}
