using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.B.2 — Windows PowerShell 5.1 launcher targeting the OS-bundled
/// <c>PowerShell.exe</c> with OEM-codepage stdout decoding.
/// </summary>
/// <remarks>
/// <para><b>Why a separate impl from <see cref="PwshCoreProcessLauncher"/></b>:
/// Windows PowerShell 5.1 (the OS-bundled <c>PowerShell.exe</c>) writes stdout
/// using the host's OEM codepage (e.g. cp437 on US English, cp936 on
/// zh-CN, cp932 on ja-JP). PowerShell Core 7+ (<c>pwsh</c>) writes UTF-8.
/// Capturing the OEM bytes via the <see cref="ProcessStartInfo.RedirectStandardOutput"/>
/// pipe with the .NET default UTF-8 decoder corrupts every non-ASCII
/// character in script output. Setting
/// <see cref="ProcessStartInfo.StandardOutputEncoding"/> to the OEM encoding
/// makes the StreamReader decode the bytes correctly and round-trip 中文 /
/// emoji / ÄÖÜ etc. through the captured-log layer unchanged.</para>
///
/// <para><b>Why <c>-File</c> instead of <c>-Command "&amp; {... exit $LastExitCode}"</c></b>:
/// PowerShell 4.0+ propagates the script's exit code via <c>-File</c> directly
/// (per <c>about_PowerShell_exe</c> docs), so the more complex <c>-Command</c>
/// scriptblock wrapper that Octopus's Calamari uses is not needed for our
/// case. The <c>-File</c> form also matches the cross-platform pwsh-Core
/// launcher shape, keeping the two paths symmetric in argv layout (only
/// FileName + encoding differ).</para>
///
/// <para><b>Why <c>-NoProfile -NoLogo -NonInteractive -ExecutionPolicy Unrestricted</c></b>:
/// these four flags are the standard "non-interactive automation" set Octopus
/// has used for ~15 years. <c>-NoProfile</c> avoids running operator
/// <c>$PROFILE.ps1</c> hooks that could leak credentials or alter execution.
/// <c>-NoLogo</c> suppresses the PowerShell banner. <c>-NonInteractive</c>
/// rejects prompts (would hang the agent forever). <c>-ExecutionPolicy
/// Unrestricted</c> bypasses the per-machine signing policy because the
/// script is short-lived and was already authorised by Squid task dispatch
/// — re-prompting at the PowerShell layer adds zero security and breaks
/// every fresh deploy.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerShellProcessLauncher : IProcessLauncher
{
    /// <summary>Pinned argv literal — the script bundle on disk is always
    /// named <c>script.ps1</c> by the PowerShell runtime bundle.</summary>
    public const string ScriptFileName = "script.ps1";

    /// <summary>PATH-fallback FileName when the canonical System32 install
    /// path doesn't exist (e.g. Nano Server, dev/test runners).</summary>
    public const string FallbackFileName = "PowerShell.exe";

    public ProcessStartInfo BuildStartInfo(string workDir, string[] arguments)
    {
        var encoding = ResolveOemEncoding();

        var psi = new ProcessStartInfo
        {
            FileName = ResolveFileName(),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Unrestricted");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptFileName);

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        return psi;
    }

    /// <summary>
    /// Resolve <c>PowerShell.exe</c> path. Tries the canonical System32
    /// install location first (<c>%SystemRoot%\System32\WindowsPowerShell\v1.0\PowerShell.exe</c>);
    /// falls back to bare <c>"PowerShell.exe"</c> on PATH for non-standard
    /// installs (Nano Server stripped images, custom mount paths) or
    /// non-Windows test runners where <see cref="Environment.SpecialFolder.System"/>
    /// returns empty.
    /// </summary>
    internal static string ResolveFileName()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

        if (!string.IsNullOrEmpty(sys))
        {
            var path = Path.Combine(sys, "WindowsPowerShell", "v1.0", FallbackFileName);

            if (File.Exists(path))
                return path;
        }

        return FallbackFileName;
    }

    /// <summary>
    /// Resolve the OEM-codepage encoding for stdout/stderr capture. On real
    /// Windows hosts <c>CultureInfo.CurrentCulture.TextInfo.OEMCodePage</c>
    /// is set by the OS (e.g. <c>437</c>, <c>936</c>, <c>932</c>) and is
    /// the codepage <c>PowerShell.exe</c> writes its stdout in. Falls back
    /// to UTF-8 if codepage isn't loadable (non-Windows test runners) so
    /// cross-platform unit tests can construct the PSI without throwing —
    /// production never hits the catch since OEM codepages are first-class
    /// on Windows .NET.
    /// </summary>
    internal static Encoding ResolveOemEncoding()
    {
        try
        {
            var oemCp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

            return Encoding.GetEncoding(oemCp);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
