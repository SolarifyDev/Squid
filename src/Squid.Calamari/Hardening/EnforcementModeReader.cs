namespace Squid.Calamari.Hardening;

/// <summary>
/// Internal copy of the project-wide enforcement-mode reader — see
/// <c>Squid.Message.Hardening.EnforcementModeReader</c>. Duplicated because
/// Squid.Calamari has no project refs. Parsing vocabulary MUST match the server copy
/// — mirror-pinned by <c>EnforcementModeReaderVocabularyMirrorTests</c> in
/// Squid.Calamari.Tests.
///
/// <para>Recognised values (case-insensitive, trimmed):
/// <c>off</c> / <c>disabled</c> / <c>0</c> / <c>false</c> → <see cref="EnforcementMode.Off"/>;
/// <c>warn</c> / <c>warning</c> → <see cref="EnforcementMode.Warn"/>;
/// <c>strict</c> / <c>enforce</c> / <c>1</c> / <c>true</c> → <see cref="EnforcementMode.Strict"/>;
/// (unset / blank / unrecognised) → <paramref name="defaultMode"/> (usually Warn).</para>
/// </summary>
public static class EnforcementModeReader
{
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
