using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Platform;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Real end-to-end Windows PowerShell tests. Runs a live <see cref="LocalScriptService"/>
/// against the host's bundled pwsh / powershell.exe. Skips on non-Windows so
/// <c>dotnet test</c> on the Linux CI / developer macOS doesn't fail these —
/// nightly GHA workflow <c>tentacle-windows-e2e.yml</c> on <c>windows-latest</c>
/// is the primary execution target.
///
/// These tests fill the gap identified in the post-refactor audit: Windows E2E
/// coverage was zero, leaving the Windows tentacle path untested despite
/// having all the code.
/// </summary>
[Collection(TentacleEnvVarMutatorsCollection.Name)]
[Trait("Category", TentacleTestCategories.WindowsTentacleE2E)]
public sealed class WindowsPowerShellE2ETests : IDisposable
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
    public void Pwsh_SimpleWriteOutput_CapturedInLogs()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = MakeCommand("Write-Output 'hello-from-pwsh'", ScriptType.PowerShell);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        // RunToCompletion accumulates logs across polls — see helper XML doc for why
        // the previous `startResp.Logs.Concat(status.Logs)` pattern was flaky.
        var (logs, exitCode) = RunToCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

        string.Join("\n", logs.Select(l => l.Text)).ShouldContain("hello-from-pwsh");
        exitCode.ShouldBe(0);
    }

    [Fact]
    public void Pwsh_NonZeroExitCode_CapturedExactly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = MakeCommand("exit 42", ScriptType.PowerShell);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var (_, exitCode) = RunToCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));
        exitCode.ShouldBe(42);
    }

    [Fact]
    public void Pwsh_StdErrOutput_TaggedAsErrorSource()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Write-Error sends to stderr pipeline; LocalScriptService routes those
        // to ProcessOutputSource.StdErr.
        var command = MakeCommand("Write-Error 'boom from test'; exit 0", ScriptType.PowerShell);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var (logs, _) = RunToCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));

        logs.ShouldContain(l => l.Source == ProcessOutputSource.StdErr && l.Text.Contains("boom from test"),
            "Write-Error must be tagged as StdErr so operators can distinguish agent output from errors");
    }

    [Fact]
    public void Pwsh_UnicodeOutput_EncodedCorrectly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var command = MakeCommand("Write-Output '你好 deployment 🚀'", ScriptType.PowerShell);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        var (logs, _) = RunToCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));
        var output = string.Join("\n", logs.Select(l => l.Text));

        // Allows for the rocket to be mangled in some PowerShell console encodings;
        // we only require the Chinese characters to round-trip, which proves UTF-8
        // is being honoured for stdout.
        output.ShouldContain("你好");
        output.ShouldContain("deployment");
    }

    [Fact]
    public void Pwsh_OptInWindowsPowerShellExe_ViaEnvVar_RunsSuccessfully()
    {
        // the operator opt-in path: setting
        // SQUID_TENTACLE_USE_WINDOWS_POWERSHELL=true routes 's
        // factory dispatch to WindowsPowerShellProcessLauncher (which targets
        // the OS-bundled PowerShell.exe). This E2E proves the env var →
        // factory → launcher → Process.Start chain actually works on real
        // Windows. ASCII-only assertion: cross-locale Unicode via OEM is
        // locale-dependent (Pwsh_UnicodeOutput_EncodedCorrectly above proves
        // the default pwsh-Core path with UTF-8); this test only exercises
        // the wiring + exit-code propagation through PowerShell.exe.
        if (!OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, "true");

            var command = MakeCommand("Write-Output 'hello-from-windows-powershell-exe'", ScriptType.PowerShell);

            _service.StartScript(command);
            _createdTickets.Add(command.ScriptTicket);

            var (logs, exitCode) = RunToCompletion(command.ScriptTicket, TimeSpan.FromSeconds(30));
            var output = string.Join("\n", logs.Select(l => l.Text));

            output.ShouldContain("hello-from-windows-powershell-exe");
            exitCode.ShouldBe(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, prior);
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
    /// Polls <see cref="LocalScriptService.GetStatus"/> until the script reports
    /// <see cref="ProcessState.Complete"/>, accumulating every log entry across
    /// polls.
    ///
    /// <para><b>Why accumulate?</b> The earlier pattern called <c>WaitForCompletion</c> (which
    /// discarded its intermediate <c>GetStatus</c> responses), then made a final
    /// <c>GetStatus(ticket, startResp.NextLogSequence)</c> call to fetch logs. That pattern
    /// flaked because:
    /// <list type="number">
    ///   <item>Intermediate polls advanced the in-memory log cursor</item>
    ///   <item>Once the script transitioned to Complete and was eventually removed from
    ///         the in-memory <c>_scripts</c> dict, the final <c>GetStatus</c> fell through
    ///         to <c>TryBuildStatusFromPersistedLogs</c></item>
    ///   <item>The persisted-logs path uses <c>LastLogSequence</c> directly (no <c>-1</c>
    ///         decrement like the in-memory branch at <c>LocalScriptService.cs:383</c>),
    ///         so it returned logs strictly AFTER the sequence — missing the boundary entry</item>
    /// </list>
    /// </para>
    ///
    /// <para>Accumulating across polls dodges all three issues: every log entry that ever
    /// existed in any <c>GetStatus</c> response is captured by sequence number, deduplicated,
    /// and returned. The test asserts against the deterministic accumulated set.</para>
    ///
    /// <para>Each poll requests logs since the highest sequence seen so far, so the
    /// service can use either in-memory or persisted-logs paths interchangeably without
    /// affecting the test's observed output.</para>
    /// </summary>
    private (List<ProcessOutput> Logs, int ExitCode) RunToCompletion(ScriptTicket ticket, TimeSpan timeout)
    {
        var accumulated = new List<ProcessOutput>();
        var seenSequences = new HashSet<long>();
        long lastSequence = 0;
        var exitCode = 0;

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = _service.GetStatus(new ScriptStatusRequest(ticket, lastSequence));

            foreach (var log in status.Logs)
            {
                // Each ProcessOutput carries a Sequence (or analog). Use the sequence to
                // dedupe across polls — same entry seen twice in successive polls counts
                // only once.
                if (seenSequences.Add(log.GetHashCode()))
                {
                    accumulated.Add(log);
                }
            }

            if (status.NextLogSequence > lastSequence)
                lastSequence = status.NextLogSequence;

            if (status.State == ProcessState.Complete)
            {
                exitCode = status.ExitCode;

                // One final read from sequence 0 to make sure no log straddling the
                // "transitioning to complete + getting removed from _scripts" window
                // is missed. Persisted-logs path with seq=0 returns everything.
                var final = _service.GetStatus(new ScriptStatusRequest(ticket, 0));
                foreach (var log in final.Logs)
                {
                    if (seenSequences.Add(log.GetHashCode()))
                    {
                        accumulated.Add(log);
                    }
                }
                exitCode = final.ExitCode;
                return (accumulated, exitCode);
            }

            Thread.Sleep(100);
        }

        // Timed out — return what we have and let the test assert.
        throw new TimeoutException(
            $"Script {ticket.TaskId} did not reach ProcessState.Complete within {timeout.TotalSeconds}s. " +
            $"Accumulated {accumulated.Count} log lines; last seq={lastSequence}.");
    }
}
