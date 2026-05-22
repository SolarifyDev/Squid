using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Platform;

/// <summary>
/// picks the platform-appropriate <see cref="IProcessLauncher"/>
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
/// <item><c>ScriptType.PowerShell</c> on Windows with operator opt-in
/// (<see cref="UseWindowsPowerShellEnvVar"/> truthy) → <see cref="WindowsPowerShellProcessLauncher"/>.</item>
/// <item><c>ScriptType.PowerShell</c> on Windows with <c>pwsh.exe</c> resolvable
/// on the host (PS7 / Core installed at the canonical MSI path or anywhere on
/// PATH) → <see cref="PwshCoreProcessLauncher"/> (default — UTF-8 Unicode
/// round-trip).</item>
/// <item><c>ScriptType.PowerShell</c> on Windows when <c>pwsh.exe</c> is NOT
/// resolvable → automatic fallback to <see cref="WindowsPowerShellProcessLauncher"/>
/// (OS-bundled PS 5.1, always present on every supported Windows release).
/// See "<b>Why auto-fallback</b>" below.</item>
/// </list>
///
/// <para><b>Why prefer pwsh-Core on Windows when available</b>: Windows
/// PowerShell 5.1 writes stdout in the host's OEM codepage (cp437 on en-US,
/// cp936 on zh-CN etc). Capturing OEM bytes via the redirect pipe with a
/// matched StandardOutputEncoding works for ASCII + locale-matching characters
/// but silently mangles cross-locale Unicode (e.g. 中文 on en-US runners
/// produces <c>"??"</c>). When pwsh-Core is installed, the factory prefers it
/// for the UTF-8-by-default stdout path.</para>
///
/// <para><b>Why auto-fallback to Windows PowerShell when pwsh is missing</b>:
/// pwsh-Core (PowerShell 7) is an OPTIONAL install on Windows — Microsoft does
/// not ship it with the OS. Without this fallback, every Windows tentacle that
/// hasn't installed PS7 crashed with <c>Win32Exception (2) "system cannot find
/// the file specified"</c> the moment any PowerShell script dispatched
/// (deployment, upgrade, health probe). The fix: probe <c>pwsh.exe</c>
/// availability and, when absent, route to the OS-bundled
/// <c>PowerShell.exe</c> 5.1 (always present on Windows Server 2016+ and
/// Win10 1607+). Stock Windows hosts work out of the box; operators who
/// install PS7 transparently get the better UTF-8 path on the next dispatch
/// (no service restart needed — probe runs per-Resolve).</para>
///
/// <para><b>Operator opt-in still wins</b>: Setting
/// <see cref="UseWindowsPowerShellEnvVar"/>=true forces Windows PowerShell
/// regardless of whether pwsh is installed — supports operators with legacy
/// modules that ONLY work on 5.1 (older WMI cmdlets, certain Active Directory
/// modules etc.).</para>
///
/// <para><b>Out of scope</b>: <c>ScriptType.Python</c> / <c>CSharp</c> / <c>FSharp</c>
/// stay on the inline <c>StartPythonProcess</c> / <c>StartCSharpProcess</c> /
/// <c>StartFSharpProcess</c> path inside <see cref="ScriptExecution.LocalScriptService"/>
/// — they have no Windows variance worth abstracting yet. Calamari similarly
/// keeps its dedicated <c>BuildCalamariProcessStartInfo</c> path because of the
/// sensitive-password env var contract pinned by B.2 tests.</para>
/// </remarks>
public static class ProcessLauncherFactory
{
    /// <summary>
    /// operator opt-in to the OS-bundled
    /// <c>PowerShell.exe</c> (Windows PowerShell 5.1) on Windows hosts. Pinned
    /// by test (Rule 8) — air-gapped operators may bake this into their
    /// systemd / sc.exe start env, renaming would silently break their setup.
    /// </summary>
    public const string UseWindowsPowerShellEnvVar = "SQUID_TENTACLE_USE_WINDOWS_POWERSHELL";

    /// <summary>
    /// Swappable seam for the <c>pwsh.exe</c> availability probe. Production
    /// uses <see cref="DefaultPwshAvailable"/> (disk lookups). Tests swap to a
    /// fixed boolean so the launcher resolution can be exercised cross-platform
    /// without depending on the test runner's actual PS7 install state.
    /// Restore via <c>try/finally</c> in tests — this is mutable static state
    /// serialised by the <c>TentacleEnvVarMutatorsCollection</c> xUnit collection.
    /// </summary>
    internal static Func<bool> PwshAvailableProbe = DefaultPwshAvailable;

    /// <summary>
    /// Resolves the launcher for <paramref name="syntax"/>. Throws for syntaxes
    /// not yet routed through this factory — call sites must explicitly opt-in.
    /// </summary>
    public static IProcessLauncher Resolve(ScriptType syntax)
    {
        return syntax switch
        {
            // Operator opt-in to Windows PowerShell 5.1 (env var truthy) — wins
            // over both the pwsh-Core preference AND the auto-fallback. Supports
            // operators with legacy modules pinned to 5.1.
            ScriptType.PowerShell when OperatingSystem.IsWindows() && IsWindowsPowerShellPreferred()
                => new WindowsPowerShellProcessLauncher(),

            // Auto-fallback (operator bug fix): on Windows without pwsh-Core
            // installed, route to the always-present OS-bundled PowerShell.exe
            // 5.1 instead of letting the dispatch crash with Win32Exception (2)
            // "file not found" at process spawn.
            ScriptType.PowerShell when OperatingSystem.IsWindows() && !PwshAvailableProbe()
                => new WindowsPowerShellProcessLauncher(),

            // Default: pwsh-Core on every host (Linux/macOS always, Windows when
            // PS7 is installed). UTF-8 stdout / cross-locale Unicode round-trip.
            ScriptType.PowerShell => new PwshCoreProcessLauncher(),

            ScriptType.Bash => new BashProcessLauncher(),
            _ => throw new NotSupportedException(
                $"ScriptType.{syntax} is not routed through ProcessLauncherFactory yet. " +
                "Python/CSharp/FSharp stay on LocalScriptService's inline Start*Process path; " +
                "Calamari uses BuildCalamariProcessStartInfo.")
        };
    }

    /// <summary>
    /// Production probe: is <c>pwsh.exe</c> resolvable on this host? Checks
    /// the canonical PS7 MSI install path (<c>%ProgramFiles%\PowerShell\7\pwsh.exe</c>)
    /// first, then walks every PATH entry. Pure <see cref="File.Exists"/> —
    /// no process spawn, no caching, ~milliseconds. Called per <see cref="Resolve"/>
    /// so an operator installing PS7 mid-deployment gets routed to the new
    /// binary on the next script dispatch without a tentacle restart.
    /// </summary>
    /// <remarks>
    /// Off-Windows: trivially returns <c>true</c> — the probe only gates the
    /// Windows auto-fallback branch in <see cref="Resolve"/>, and Linux/macOS
    /// have no PS 5.1 to fall back to (PowerShell.exe doesn't exist there).
    /// Returning <c>true</c> off-Windows means the default probe is a no-op on
    /// cross-platform test runners, with no spurious disk I/O.
    /// </remarks>
    internal static bool DefaultPwshAvailable()
    {
        if (!OperatingSystem.IsWindows()) return true;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (!string.IsNullOrEmpty(programFiles))
        {
            var canonical = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");

            if (File.Exists(canonical)) return true;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathEnv)) return false;

        foreach (var rawDir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(rawDir)) continue;

            try
            {
                var candidate = Path.Combine(rawDir.Trim(), "pwsh.exe");

                if (File.Exists(candidate)) return true;
            }
            catch
            {
                // Malformed PATH entry (invalid chars, locked dir, etc.) — skip.
            }
        }

        return false;
    }

    /// <summary>
    /// Reads <see cref="UseWindowsPowerShellEnvVar"/> as a permissive boolean —
    /// case-insensitive accept of <c>"1" / "true" / "yes" / "on"</c> with
    /// surrounding whitespace trimmed (cmd.exe's <c>set FOO= true</c> is a
    /// common operator typo); everything else (including unset / empty /
    /// whitespace-only) is treated as false. Internal so the test suite can
    /// spy on the env-var name without exposing parsing logic.
    /// </summary>
    internal static bool IsWindowsPowerShellPreferred()
    {
        var raw = Environment.GetEnvironmentVariable(UseWindowsPowerShellEnvVar)?.Trim();

        if (string.IsNullOrEmpty(raw)) return false;

        return raw.Equals("1", StringComparison.Ordinal)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
