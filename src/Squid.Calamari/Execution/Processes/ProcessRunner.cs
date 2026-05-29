using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Squid.Calamari.Execution.Output;

namespace Squid.Calamari.Execution.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// Optional wall-clock ceiling for a single Calamari child process, in whole minutes.
    /// Unset / non-numeric / non-positive => no Calamari-local timeout (the process runs
    /// until it exits or the caller's <see cref="CancellationToken"/> fires — the historical
    /// behaviour, preserved so long-running deployments are never killed by default).
    ///
    /// <para>Set to a positive integer to have the runner terminate a child that overruns,
    /// so a hung script (one blocked on stdin, a <c>kubectl</c> that never returns, etc.)
    /// can't pin the work directory or the calling thread forever. Resolved at call time so
    /// operators can change it between deployments without restarting the agent.</para>
    /// </summary>
    public const string ScriptTimeoutMinutesEnvVar = "SQUID_CALAMARI_SCRIPT_TIMEOUT_MINUTES";

    /// <summary>
    /// Exit code returned when a process is killed for exceeding the timeout. Mirrors the
    /// coreutils <c>timeout</c> convention (124) so logs and downstream tooling read it as
    /// "terminated for overrunning its deadline", not a script-authored failure.
    /// </summary>
    internal const int TimeoutExitCode = 124;

    public Task<ProcessResult> ExecuteAsync(ProcessInvocation invocation, IProcessOutputSink outputSink, CancellationToken ct)
        => ExecuteAsync(invocation, outputSink, ResolveTimeout(), ct);

    internal async Task<ProcessResult> ExecuteAsync(ProcessInvocation invocation, IProcessOutputSink outputSink, TimeSpan? timeout, CancellationToken ct)
    {
        using var process = CreateProcess(invocation);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputSink.WriteStdout(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                outputSink.WriteStderr(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return timeout is { } limit
            ? await WaitWithTimeoutAsync(process, invocation, outputSink, limit, ct).ConfigureAwait(false)
            : await WaitAsync(process, ct).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> WaitAsync(Process process, CancellationToken ct)
    {
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode);
    }

    private static async Task<ProcessResult> WaitWithTimeoutAsync(
        Process process, ProcessInvocation invocation, IProcessOutputSink outputSink, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            return new ProcessResult(process.ExitCode);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            KillProcessTree(process);

            await WaitForExitBestEffortAsync(process).ConfigureAwait(false);

            outputSink.WriteStderr(
                $"Process '{invocation.Executable}' exceeded the {timeout.TotalMinutes:0.##}-minute Calamari script timeout and was terminated. " +
                $"Adjust or disable it via the {ScriptTimeoutMinutesEnvVar} environment variable.");

            return new ProcessResult(TimeoutExitCode);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process exited between the HasExited check and the kill — nothing to terminate.
        }
        catch (Win32Exception)
        {
            // OS refused the kill (already dying / insufficient rights) — best-effort only.
        }
    }

    private static async Task WaitForExitBestEffortAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Tree didn't fully reap within the grace window; we've done our best.
        }
    }

    internal static TimeSpan? ResolveTimeout()
        => ParseTimeoutMinutes(Environment.GetEnvironmentVariable(ScriptTimeoutMinutesEnvVar));

    internal static TimeSpan? ParseTimeoutMinutes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return null;
        if (minutes <= 0) return null;

        return TimeSpan.FromMinutes(minutes);
    }

    private static Process CreateProcess(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.Executable,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in invocation.Arguments)
            startInfo.ArgumentList.Add(arg);

        if (invocation.EnvironmentVariables != null)
        {
            foreach (var (key, value) in invocation.EnvironmentVariables)
                startInfo.Environment[key] = value;
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }
}
