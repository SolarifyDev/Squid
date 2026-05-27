using Squid.Calamari.Execution.Output;
using Squid.Calamari.Execution.Processes;

namespace Squid.Calamari.Execution;

/// <summary>
/// PR-4 — Executes a PowerShell script file, streaming stdout/stderr
/// through <see cref="ScriptOutputProcessor"/>. Sibling of
/// <see cref="BashScriptExecutor"/> for the bash branch.
///
/// <para><b>Executable resolution</b> — prefers <c>pwsh</c> (PowerShell
/// 7+, cross-platform), falls back to <c>powershell.exe</c> on Windows
/// (Windows PowerShell 5.1, always present on Win10+). Linux / macOS
/// without <c>pwsh</c> get a clear "PowerShell not installed" error —
/// no fallback (no <c>powershell.exe</c> exists outside Windows).</para>
///
/// <para><b>Arguments</b> match the established Squid pattern:
/// <c>-NoProfile</c> (skip operator's profile.ps1, defends against
/// machine-local env pollution) + <c>-NonInteractive</c> (refuse
/// Read-Host prompts that would hang the deploy) + <c>-File &lt;path&gt;</c>.</para>
/// </summary>
public class PowerShellScriptExecutor
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _pwshResolver;

    public PowerShellScriptExecutor()
        : this(new ProcessRunner(), ResolvePowerShellBinary)
    {
    }

    public PowerShellScriptExecutor(IProcessRunner processRunner)
        : this(processRunner, ResolvePowerShellBinary)
    {
    }

    /// <summary>Test seam — lets tests inject the resolved binary path.</summary>
    internal PowerShellScriptExecutor(IProcessRunner processRunner, Func<string?> pwshResolver)
    {
        _processRunner = processRunner;
        _pwshResolver = pwshResolver;
    }

    public async Task<int> ExecuteAsync(
        string scriptPath,
        string workDir,
        ScriptOutputProcessor outputProcessor,
        CancellationToken ct)
    {
        var binary = _pwshResolver()
                     ?? throw new InvalidOperationException(
                         "PowerShell executor: neither 'pwsh' nor 'powershell.exe' (Windows-only fallback) was found on PATH. " +
                         "Install PowerShell 7+ (https://aka.ms/install-powershell) on this agent OR run the script as bash by " +
                         "renaming it to .sh.");

        var invocation = new ProcessInvocation(
            executable: binary,
            arguments: ["-NoProfile", "-NonInteractive", "-File", scriptPath],
            workingDirectory: workDir);

        var result = await _processRunner.ExecuteAsync(invocation, outputProcessor.OutputSink, ct)
            .ConfigureAwait(false);

        return result.ExitCode;
    }

    /// <summary>
    /// Find pwsh / powershell.exe on PATH. Returns the absolute path of
    /// the first one found, or null if neither exists.
    ///
    /// <para>Probe order: <c>pwsh</c> first (cross-platform, modern),
    /// then <c>powershell.exe</c> on Windows only (legacy 5.1 fallback —
    /// always present on Win10+). This mirrors the 1.7.x Tentacle
    /// fallback established in PR #352.</para>
    /// </summary>
    public static string? ResolvePowerShellBinary()
    {
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is not null) return pwsh;

        if (OperatingSystem.IsWindows())
        {
            var legacy = FindOnPath("powershell.exe");
            if (legacy is not null) return legacy;
        }

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';

        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Invalid path chars in PATH segment — skip silently. Operator
                // has a corrupt PATH; we just don't find pwsh through that segment.
            }
        }

        return null;
    }
}
