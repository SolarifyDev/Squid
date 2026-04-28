using System.Diagnostics;

namespace Squid.Tentacle.Platform;

/// <summary>
/// P1-Phase12.B — narrow per-platform shaper of the <see cref="ProcessStartInfo"/>
/// used to launch a user script. Lifecycle (Start, EnableRaisingEvents, output
/// pump, kill-on-cancel, drain) stays in <see cref="ScriptExecution.LocalScriptService"/>;
/// the launcher's only job is "build the PSI right for THIS platform + THIS syntax".
/// </summary>
/// <remarks>
/// <para><b>Why it's this narrow:</b> the existing call site in
/// <see cref="ScriptExecution.LocalScriptService.StartProcess"/> already owns the
/// kill-tree, the manifest, the output pump, the soft-cancel registry. Pulling
/// any of that into the launcher would force every platform impl to re-implement
/// the same lifecycle machinery — exactly the duplication we want to avoid.</para>
///
/// <para><b>Why <see cref="ProcessStartInfo"/> instead of "launch and return Process":</b>
/// keeping the launcher pure (no <see cref="Process.Start()"/>) makes it
/// trivially unit-testable on every platform. We can pin the argv literal,
/// encoding, working-dir, redirect flags etc. without spawning anything.</para>
///
/// <para><b>Calamari is NOT routed through this interface</b> — it has its own
/// special argv (run-script + variables.json + sensitive password env var) that
/// already lives in <c>BuildCalamariProcessStartInfo</c>. Phase B intentionally
/// keeps Calamari, Python, CSharp, FSharp on the inline path because they have
/// no Windows variance worth abstracting yet (Phase C may revisit).</para>
/// </remarks>
public interface IProcessLauncher
{
    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> for a script already authored
    /// to disk inside <paramref name="workDir"/> (e.g. <c>script.sh</c> for
    /// Bash, <c>script.ps1</c> for PowerShell). Caller is responsible for
    /// <c>new Process { StartInfo = psi, EnableRaisingEvents = true }.Start()</c>.
    /// </summary>
    /// <param name="workDir">Per-ticket work directory; becomes
    /// <see cref="ProcessStartInfo.WorkingDirectory"/>.</param>
    /// <param name="arguments">Optional user-supplied args appended after the
    /// script-file argument. Caller passes <c>command.Arguments</c> from
    /// <c>StartScriptCommand</c>.</param>
    ProcessStartInfo BuildStartInfo(string workDir, string[] arguments);
}
