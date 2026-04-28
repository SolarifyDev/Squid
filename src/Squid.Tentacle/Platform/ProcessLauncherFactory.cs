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
/// <item><c>ScriptType.PowerShell</c> on Windows → <see cref="WindowsPowerShellProcessLauncher"/>
/// (PowerShell.exe + OEM stdout decoding for non-ASCII round-trip).</item>
/// <item><c>ScriptType.PowerShell</c> on Linux / macOS → <see cref="PwshCoreProcessLauncher"/>
/// (pwsh on PATH + UTF-8 stdout).</item>
/// </list>
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
    /// Resolves the launcher for <paramref name="syntax"/>. Throws for syntaxes
    /// not yet routed through this factory — call sites must explicitly opt-in.
    /// </summary>
    public static IProcessLauncher Resolve(ScriptType syntax)
    {
        return syntax switch
        {
            ScriptType.PowerShell when OperatingSystem.IsWindows() => new WindowsPowerShellProcessLauncher(),
            ScriptType.PowerShell => new PwshCoreProcessLauncher(),
            ScriptType.Bash => new BashProcessLauncher(),
            _ => throw new NotSupportedException(
                $"ScriptType.{syntax} is not routed through ProcessLauncherFactory yet. " +
                "Python/CSharp/FSharp stay on LocalScriptService's inline Start*Process path; " +
                "Calamari uses BuildCalamariProcessStartInfo.")
        };
    }
}
