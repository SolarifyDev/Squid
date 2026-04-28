using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.B.1 — picks the platform-appropriate <see cref="IProcessLauncher"/>
/// for a given <see cref="ScriptType"/>. Static factory matching the
/// <see cref="FilePermissionManagerFactory"/> + <see cref="UpgradeStatusStorageFactory"/>
/// + <see cref="ServiceUserProviderFactory"/> convention.
/// </summary>
/// <remarks>
/// <para><b>Routing</b>:</para>
/// <list type="bullet">
/// <item><c>ScriptType.Bash</c> → <see cref="BashProcessLauncher"/> on every OS.</item>
/// <item><c>ScriptType.PowerShell</c> on Linux / macOS → <see cref="PwshCoreProcessLauncher"/>
/// (pwsh on PATH + UTF-8 stdout).</item>
/// <item><c>ScriptType.PowerShell</c> on Windows → <see cref="PwshCoreProcessLauncher"/>
/// (default — preserves pre-Phase-12 behaviour and UTF-8 Unicode round-trip).
/// Operators who specifically want the OS-bundled <c>PowerShell.exe</c>
/// (Windows PowerShell 5.1) opt in via env var <see cref="UseWindowsPowerShellEnvVar"/>
/// — covered by <see cref="WindowsPowerShellProcessLauncher"/>.</item>
/// </list>
///
/// <para><b>Why default to pwsh-Core on Windows</b>: Windows PowerShell 5.1
/// writes stdout in the host's OEM codepage (cp437 on en-US, cp936 on zh-CN
/// etc). Capturing OEM bytes via the redirect pipe with a matched
/// StandardOutputEncoding works for ASCII + locale-matching characters but
/// silently mangles cross-locale Unicode (e.g. 中文 on en-US runners produces
/// <c>"??"</c>). Phase-12.B keeps the modern, UTF-8-by-default pwsh-Core path
/// as the safe default; a future phase can address the encoding strategy
/// (e.g. <c>chcp 65001</c> bootstrap or <c>$OutputEncoding=[Text.Encoding]::UTF8</c>
/// prepend) before promoting Windows PowerShell to the default.</para>
///
/// <para><b>Out of scope</b>: <c>ScriptType.Python</c> / <c>CSharp</c> / <c>FSharp</c>
/// stay on the inline <c>StartPythonProcess</c> / <c>StartCSharpProcess</c> /
/// <c>StartFSharpProcess</c> path inside <see cref="ScriptExecution.LocalScriptService"/>
/// — they have no Windows variance worth abstracting yet. Calamari similarly
/// keeps its dedicated <c>BuildCalamariProcessStartInfo</c> path because of the
/// sensitive-password env var contract pinned by Phase-2 B.2 tests.</para>
/// </remarks>
public static class ProcessLauncherFactory
{
    /// <summary>
    /// P1-Phase12.B.3 — operator opt-in to the OS-bundled
    /// <c>PowerShell.exe</c> (Windows PowerShell 5.1) on Windows hosts. Pinned
    /// by test (Rule 8) — air-gapped operators may bake this into their
    /// systemd / sc.exe start env, renaming would silently break their setup.
    /// </summary>
    public const string UseWindowsPowerShellEnvVar = "SQUID_TENTACLE_USE_WINDOWS_POWERSHELL";

    /// <summary>
    /// Resolves the launcher for <paramref name="syntax"/>. Throws for syntaxes
    /// not yet routed through this factory — call sites must explicitly opt-in.
    /// </summary>
    public static IProcessLauncher Resolve(ScriptType syntax)
    {
        return syntax switch
        {
            ScriptType.PowerShell when OperatingSystem.IsWindows() && IsWindowsPowerShellPreferred()
                => new WindowsPowerShellProcessLauncher(),
            ScriptType.PowerShell => new PwshCoreProcessLauncher(),
            ScriptType.Bash => new BashProcessLauncher(),
            _ => throw new NotSupportedException(
                $"ScriptType.{syntax} is not routed through ProcessLauncherFactory yet. " +
                "Python/CSharp/FSharp stay on LocalScriptService's inline Start*Process path; " +
                "Calamari uses BuildCalamariProcessStartInfo.")
        };
    }

    /// <summary>
    /// Reads <see cref="UseWindowsPowerShellEnvVar"/> as a permissive boolean —
    /// case-insensitive accept of <c>"1" / "true" / "yes" / "on"</c>; everything
    /// else (including unset / empty) is treated as false. Internal so the
    /// test suite can spy on the env-var name without exposing parsing logic.
    /// </summary>
    internal static bool IsWindowsPowerShellPreferred()
    {
        var raw = Environment.GetEnvironmentVariable(UseWindowsPowerShellEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        return raw.Equals("1", StringComparison.Ordinal)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
