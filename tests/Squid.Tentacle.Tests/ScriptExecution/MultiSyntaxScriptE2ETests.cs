using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// End-to-end coverage for the multi-syntax script execution path on a real
/// Tentacle. Each test self-skips when the underlying interpreter
/// (<c>python3</c> / <c>dotnet-script</c> / <c>dotnet fsi</c>) is not
/// installed on the host so the suite stays green on minimal runners; the
/// production <c>squid-tentacle-linux</c> image installs all three.
///
/// Closes the gap that produced the 2026-04-18 regression: the same Python
/// script that ran fine targeting a Kubernetes API target failed on a
/// Tentacle target with <c>"import: command not found"</c> because the
/// Tentacle-side <c>ScriptType</c> enum used to collapse Python into Bash,
/// silently downgrading the script body.
/// </summary>
[Trait("Category", TentacleTestCategories.MultiSyntaxE2E)]
public sealed class MultiSyntaxScriptE2ETests : IDisposable
{
    private readonly LocalScriptService _service = new();
    private readonly List<ScriptTicket> _createdTickets = new();

    public void Dispose()
    {
        foreach (var t in _createdTickets)
        {
            try { _service.CancelScript(new CancelScriptCommand(t, 0)); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Python_PrintHello_CapturedInLogs()
    {
        // Pre-existing flakiness fixed here (sibling to 980dfe3): under parallel
        // CI load, Python can exit before the async OutputDataReceived handlers
        // drain the final `print` lines, leaving GetStatus with empty logs.
        // Two-layer fix matching Python_StdErrOutput_TaggedAsErrorSource:
        //   1. flush=True — defeat Python's line-buffering race with exit.
        //   2. Poll GetStatus with a cursor until BOTH expected lines surface
        //      (or hit 30s timeout) — decouples from process Exited timing.
        if (!IsCommandAvailable("python3", "--version")) return;

        var command = MakeCommand(
            "import sys\nprint('hello-from-python', flush=True)\nprint(f'major={sys.version_info.major}', flush=True)",
            ScriptType.Python);
        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var logs = WaitForLogsContainingAll(command.ScriptTicket, new[] { "hello-from-python", "major=3" }, TimeSpan.FromSeconds(30));

        var allText = string.Join("\n", logs.Select(l => l.Text));

        allText.ShouldContain("hello-from-python");
        allText.ShouldContain("major=3");

        // Exit code is available once the script has run — GetStatus gives it
        // via status.ExitCode; we read a final status to confirm success.
        var finalStatus = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));
        finalStatus.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void Python_NonZeroExit_PropagatesExactExitCode()
    {
        if (!IsCommandAvailable("python3", "--version")) return;

        var output = RunAndCollectOutput("import sys; sys.exit(7)", ScriptType.Python);

        output.exitCode.ShouldBe(7);
    }

    [Fact]
    public void Python_StdErrOutput_TaggedAsErrorSource()
    {
        if (!IsCommandAvailable("python3", "--version")) return;

        // Use flush=True to defeat Python's line-buffering on stderr/stdout
        // race with process exit. Without this, under load (full test suite
        // running in parallel) the process can exit before the async
        // OutputDataReceived/ErrorDataReceived events drain, dropping lines.
        var command = MakeCommand(
            "import sys\nprint('stderr-line', file=sys.stderr, flush=True)\nprint('stdout-line', flush=True)",
            ScriptType.Python);
        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        // Poll up to 30s for BOTH expected lines to surface (whichever arrives
        // last wins). Decoupled from process Exited event so we don't race the
        // async log drain.
        var logs = WaitForBothLogLines(command.ScriptTicket, "stderr-line", "stdout-line", TimeSpan.FromSeconds(30));

        logs.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("stderr-line"),
            "Python stderr writes must surface as ProcessOutputSource.StdErr");
        logs.ShouldContain(l => l.Source == ProcessOutputSource.StdOut && l.Text.Contains("stdout-line"));
    }

    private List<ProcessOutput> WaitForBothLogLines(ScriptTicket ticket, string stderrSubstring, string stdoutSubstring, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var collected = new List<ProcessOutput>();
        long cursor = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = _service.GetStatus(new ScriptStatusRequest(ticket, cursor));
            cursor = status.NextLogSequence;
            collected.AddRange(status.Logs);

            var hasStderr = collected.Any(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains(stderrSubstring, StringComparison.Ordinal));
            var hasStdout = collected.Any(l => l.Source == ProcessOutputSource.StdOut && l.Text.Contains(stdoutSubstring, StringComparison.Ordinal));
            if (hasStderr && hasStdout) return collected;

            Thread.Sleep(100);
        }

        return collected;
    }

    [Fact]
    public void Python_UnbufferedOutput_StreamsBeforeProcessExits()
    {
        // Confirms we set PYTHONUNBUFFERED=1 — without it `print()` buffers
        // until process exit, which would defeat the live log streaming the
        // operator UI relies on. The sleep proves the line appears BEFORE the
        // script naturally finishes.
        //
        // Timing margin: python3 cold-startup + first print + async-log-drain
        // + GetStatus polling roundtrip can easily exceed 1.5s on a loaded CI
        // runner (measured ~1.8s on GitHub Actions ubuntu-latest under load).
        // 3s sleep with 2500ms wait leaves ~500ms of buffer between "we observe
        // early-line" and "late-line would appear" — still proves streaming
        // without racing the scheduler.
        if (!IsCommandAvailable("python3", "--version")) return;

        var script = "import time\nprint('early-line', flush=False)\ntime.sleep(3)\nprint('late-line', flush=False)";
        var command = MakeCommand(script, ScriptType.Python);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var observed = WaitForLogContaining(command.ScriptTicket, "early-line", TimeSpan.FromMilliseconds(2500));

        observed.ShouldBeTrue("early-line must stream before the script's 3s sleep finishes (PYTHONUNBUFFERED=1)");
    }

    [Fact]
    public void CSharp_PrintHello_CapturedInLogs()
    {
        // Same async-log-drain + interpreter-startup-race pattern as Python /
        // F# — Console.WriteLine is line-buffered and `dotnet-script` startup
        // is slow on a loaded CI runner. Use the polling helper rather than
        // RunAndCollectOutput so the test doesn't race the OutputDataReceived
        // drain. Console.Out.Flush() defeats the line-buffer vs exit race on
        // top of that.
        if (!IsCommandAvailable("dotnet-script", "--version")) return;

        var command = MakeCommand(
            """
            Console.WriteLine("hello-from-csharp");
            Console.Out.Flush();
            """,
            ScriptType.CSharp);
        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var logs = WaitForLogsContainingAll(command.ScriptTicket, new[] { "hello-from-csharp" }, TimeSpan.FromSeconds(60));
        var allText = string.Join("\n", logs.Select(l => l.Text));

        allText.ShouldContain("hello-from-csharp");

        var finalStatus = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));
        finalStatus.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void FSharp_PrintHello_CapturedInLogs()
    {
        // Same async-log-drain race as Python_PrintHello (see line 42-71 above) —
        // `dotnet fsi` startup is slow and `printfn` is line-buffered, so on a
        // loaded CI runner the process can exit before the OutputDataReceived
        // handlers drain the final stdout line, leaving a clean Complete state
        // with empty logs. Two-layer fix mirroring the Python case:
        //   1. F# script flushes stdout explicitly — defeats `printfn`'s line
        //      buffering vs process-exit race.
        //   2. Poll GetStatus with a cursor until the expected substring lands
        //      (or 60s timeout) — decouples from the process Exited timing.
        if (!IsCommandAvailable("dotnet", "fsi", "--version")) return;

        var command = MakeCommand(
            """
            printfn "hello-from-fsharp"
            System.Console.Out.Flush()
            """,
            ScriptType.FSharp);
        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var logs = WaitForLogsContainingAll(command.ScriptTicket, new[] { "hello-from-fsharp" }, TimeSpan.FromSeconds(60));
        var allText = string.Join("\n", logs.Select(l => l.Text));

        allText.ShouldContain("hello-from-fsharp");

        var finalStatus = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));
        finalStatus.ExitCode.ShouldBe(0);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private (string allText, int exitCode) RunAndCollectOutput(string script, ScriptType syntax)
    {
        var data = RunAndCollectLogs(script, syntax);
        return (string.Join("\n", data.logs.Select(l => l.Text)), data.exitCode);
    }

    private (List<ProcessOutput> logs, int exitCode) RunAndCollectLogs(string script, ScriptType syntax)
    {
        var command = MakeCommand(script, syntax);

        var startResp = _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        WaitForCompletion(command.ScriptTicket, TimeSpan.FromSeconds(60));

        var status = _service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, startResp.NextLogSequence));
        return (startResp.Logs.Concat(status.Logs).ToList(), status.ExitCode);
    }

    /// <summary>
    /// Polls GetStatus until every expected substring has appeared in the
    /// collected log stream (or the timeout is hit). Source-agnostic — unlike
    /// <see cref="WaitForBothLogLines"/>, all substrings can be on the same
    /// source (both stdout, for example). Drains the log cursor on each poll
    /// so nothing is re-fetched; returns as soon as every substring is seen.
    /// </summary>
    private List<ProcessOutput> WaitForLogsContainingAll(ScriptTicket ticket, IReadOnlyList<string> substrings, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var collected = new List<ProcessOutput>();
        long cursor = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = _service.GetStatus(new ScriptStatusRequest(ticket, cursor));
            cursor = status.NextLogSequence;
            collected.AddRange(status.Logs);

            if (substrings.All(s => collected.Any(l => l.Text.Contains(s, StringComparison.Ordinal))))
                return collected;

            Thread.Sleep(100);
        }

        return collected;
    }

    private bool WaitForLogContaining(ScriptTicket ticket, string substring, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        long cursor = 0;
        var seen = false;

        while (DateTimeOffset.UtcNow < deadline && !seen)
        {
            var status = _service.GetStatus(new ScriptStatusRequest(ticket, cursor));
            cursor = status.NextLogSequence;
            seen = status.Logs.Any(l => l.Text.Contains(substring, StringComparison.Ordinal));
            if (!seen) Thread.Sleep(100);
        }

        return seen;
    }

    private void WaitForCompletion(ScriptTicket ticket, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));
            if (status.State == ProcessState.Complete) return;
            Thread.Sleep(100);
        }
    }

    private static StartScriptCommand MakeCommand(string scriptBody, ScriptType syntax)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero)
        {
            ScriptSyntax = syntax
        };
    }

    /// <summary>
    /// Probes whether an interpreter is on PATH by running its --version flag.
    /// Returns false on any failure so tests skip cleanly on minimal runners.
    /// </summary>
    private static bool IsCommandAvailable(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            return proc.WaitForExit(5_000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
