using System.Diagnostics;
using System.Text;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.B.1 — PowerShell Core launcher (<c>pwsh</c> binary on PATH).
/// Used on Linux + macOS for <c>ScriptType.PowerShell</c>; Windows uses the
/// dedicated <see cref="WindowsPowerShellProcessLauncher"/> (Phase 12.B.2)
/// which targets the OS-bundled <c>PowerShell.exe</c> with OEM-codepage
/// stdout decoding instead.
/// </summary>
/// <remarks>
/// <para><b>UTF-8 stdout</b>: PowerShell Core's default stdout encoding is
/// UTF-8 on every supported host (unlike Windows PowerShell 5.1 which writes
/// OEM). Forcing UTF-8 here makes non-ASCII output (中文 / emoji /
/// accented chars) round-trip correctly through the captured-log layer.</para>
///
/// <para><b>Argv shape</b>: <c>pwsh -File script.ps1 [...userArgs]</c>. The
/// <c>-File</c> form is intentionally simpler than the Windows path's
/// <c>-Command "&amp; { . './script.ps1'; exit $LastExitCode }"</c> wrapper
/// because pwsh-Core auto-propagates the script's exit code as the host's
/// <c>$LastExitCode</c>; the wrapper is only needed for Windows PowerShell
/// 5.1 where invoking via <c>-File</c> with a relative path can lose the
/// exit code in some edge cases.</para>
/// </remarks>
public sealed class PwshCoreProcessLauncher : IProcessLauncher
{
    /// <summary>Pinned argv literal — the script bundle on disk is always
    /// named <c>script.ps1</c> by the PowerShell runtime bundle.</summary>
    public const string ScriptFileName = "script.ps1";

    public ProcessStartInfo BuildStartInfo(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Force UTF-8 so non-ASCII output (Chinese, emoji, etc.) round-trips
            // correctly. PowerShell Core defaults to UTF-8 already; pinning this
            // explicitly survives any future host-locale-dependent default.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptFileName);

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        return psi;
    }
}
