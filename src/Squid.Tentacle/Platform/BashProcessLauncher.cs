using System.Diagnostics;
using System.Text;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.B.1 — Bash launcher. Cross-platform (Linux, macOS, WSL on Windows).
/// Extracted bit-for-bit from the pre-Phase-12 <c>LocalScriptService.StartBashProcess</c>;
/// every PSI field preserved exactly so existing real-bash tests stay green
/// without modification.
/// </summary>
public sealed class BashProcessLauncher : IProcessLauncher
{
    /// <summary>Pinned argv literal — the script bundle on disk is always
    /// named <c>script.sh</c> by <c>BashRuntimeBundle</c>.</summary>
    public const string ScriptFileName = "script.sh";

    public ProcessStartInfo BuildStartInfo(string workDir, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add(ScriptFileName);

        if (arguments != null)
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

        return psi;
    }
}
