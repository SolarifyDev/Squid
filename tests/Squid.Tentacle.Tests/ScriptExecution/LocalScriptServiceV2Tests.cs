using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/>, real bash processes, real state files in the real
/// temp workspace and a real tombstone directory. A "restart" is a new service instance over the same workspace and
/// tombstone directory while the first instance is abandoned, which is what a restarted agent sees.
/// </summary>
[Trait("Category", "LinuxTentacleScriptServiceV2E2E")]
public class LocalScriptServiceV2Tests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly string _tombstoneDir = Path.Combine(Path.GetTempPath(), $"squid-v2-tombstones-{Guid.NewGuid():N}");
    private readonly List<string> _tickets = new();
    private readonly List<LocalScriptService> _services = new();

    // ── state transitions ───────────────────────────────────────────────────

    [Fact]
    public void Start_LongScript_ReportsRunning_ThenCompletedWithRealExitCodeAndLogs()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewCommand("echo first-line; sleep 1; echo last-line; exit 7");

        var started = service.StartScript(command);
        var final = WaitForCompleted(service, command.ScriptTicket);

        started.State.ShouldBe(ScriptStateV2.Running);
        final.ExitCode.ShouldBe(7);
        Text(final).ShouldContain("first-line");
        Text(final).ShouldContain("last-line");
    }

    [Fact]
    public void GetStatus_HonoursLogCursor_NoLineIsDeliveredTwice()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewCommand("echo a; echo b; echo c");
        service.StartScript(command);
        var all = WaitForCompleted(service, command.ScriptTicket);

        var tail = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, all.NextLogSequence));

        all.Logs.Count.ShouldBe(3);
        tail.Logs.ShouldBeEmpty();
        tail.NextLogSequence.ShouldBe(all.NextLogSequence);
    }

    [Fact]
    public void StartThenCompleteThenComplete_FirstCompletedWithLogs_SecondAlreadyCompleted_WorkspaceReleased()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewCommand("echo bye; exit 3");
        service.StartScript(command);
        WaitForCompleted(service, command.ScriptTicket);

        var first = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var second = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));

        first.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        first.FinalStatus!.ExitCode.ShouldBe(3);
        Text(first.FinalStatus).ShouldContain("bye");
        second.Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
        second.FinalStatus.ShouldBeNull();
        Directory.Exists(WorkDir(command.ScriptTicket)).ShouldBeFalse("Complete must release the workspace");
    }

    // ── idempotent start ────────────────────────────────────────────────────

    [Fact]
    public void Start_Twice_RunsOneProcess_EvenWhenSecondCommandDiffers()
    {
        if (OperatingSystem.IsWindows()) return;

        var marker = Path.Combine(Path.GetTempPath(), $"squid-v2-marker-{Guid.NewGuid():N}");
        var service = NewService();
        var first = NewCommand($"echo run >> {marker}; sleep 2");
        var second = new StartScriptCommandV2 { ScriptTicket = first.ScriptTicket, ScriptBody = $"echo SECOND >> {marker}", ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

        try
        {
            service.StartScript(first);
            var repeat = service.StartScript(second);
            Thread.Sleep(500);

            repeat.State.ShouldBe(ScriptStateV2.Running, "a repeated Start returns the current status");
            File.ReadAllLines(marker).ShouldBe(new[] { "run" }, "the script must have run exactly once, never the second body");
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public void Start_ConcurrentDuplicates_RunOneProcess()
    {
        if (OperatingSystem.IsWindows()) return;

        var marker = Path.Combine(Path.GetTempPath(), $"squid-v2-marker-{Guid.NewGuid():N}");
        var service = NewService();
        var command = NewCommand($"echo run >> {marker}; sleep 2");

        try
        {
            Parallel.For(0, 12, _ => service.StartScript(command));
            Thread.Sleep(500);

            File.ReadAllLines(marker).Length.ShouldBe(1, "concurrent redeliveries of one ticket must collapse to a single execution");
        }
        finally { File.Delete(marker); }
    }

    [Fact]
    public void Start_RepeatAfterRestart_WithDeadPidAndNoExit_ReportsResultLost_NeverRerun()
    {
        var command = NewCommand("echo should-not-run");
        SaveState(command.ScriptTicket, new ScriptState { TicketId = command.ScriptTicket.TaskId, Progress = ScriptProgress.Running, ProcessId = int.MaxValue, CreatedAt = DateTimeOffset.UtcNow });

        var status = NewService().StartScript(command);

        status.State.ShouldBe(ScriptStateV2.ResultLost);
        File.Exists(Path.Combine(WorkDir(command.ScriptTicket), "script.sh")).ShouldBeFalse("a ticket the agent already knew must not be started again");
    }

    // ── unknown / lost ──────────────────────────────────────────────────────

    [Fact]
    public void UnknownTicket_GetStatusCancelComplete_AreExplicit_NoFakeExitCode()
    {
        var service = NewService();
        var ticket = NewTicket();

        var status = service.GetStatus(new ScriptStatusRequestV2(ticket, 0));
        var cancel = service.CancelScript(new CancelScriptCommandV2(ticket, 0));
        var complete = service.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

        status.State.ShouldBe(ScriptStateV2.UnknownTicket);
        status.HasExitCode().ShouldBeFalse();
        cancel.State.ShouldBe(ScriptStateV2.UnknownTicket);
        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.UnknownTicket);
    }

    [Fact]
    public void RestartWithRunningStateAndDeadPid_ReportsResultLost_AndCompleteReleasesOnce()
    {
        var ticket = NewTicket();
        SaveState(ticket, new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, ProcessId = int.MaxValue, CreatedAt = DateTimeOffset.UtcNow });
        var restarted = NewService();

        var status = restarted.GetStatus(new ScriptStatusRequestV2(ticket, 0));
        var first = restarted.CompleteScript(new CompleteScriptCommandV2(ticket, 0));
        var second = restarted.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

        status.State.ShouldBe(ScriptStateV2.ResultLost);
        first.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        first.FinalStatus!.State.ShouldBe(ScriptStateV2.ResultLost);
        second.Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
    }

    [Fact]
    public void RestartWithUnreadableState_ReportsResultLost_NotUnknown()
    {
        var ticket = NewTicket();
        Directory.CreateDirectory(WorkDir(ticket));
        File.WriteAllText(Path.Combine(WorkDir(ticket), "scriptstate.json"), "{ this is not json");

        NewService().GetStatus(new ScriptStatusRequestV2(ticket, 0)).State.ShouldBe(ScriptStateV2.ResultLost);
    }

    [Fact]
    public void RestartWithRunningStateAndLivePid_ReportsRunning()
    {
        if (OperatingSystem.IsWindows()) return;

        using var sleeper = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        var ticket = NewTicket();
        SaveState(ticket, new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, ProcessId = sleeper.Id, ProcessStartedAt = sleeper.StartTime.ToUniversalTime(), CreatedAt = DateTimeOffset.UtcNow });

        try
        {
            NewService().GetStatus(new ScriptStatusRequestV2(ticket, 0)).State.ShouldBe(ScriptStateV2.Running);
        }
        finally { sleeper.Kill(); }
    }

    [Fact]
    public void Complete_ScriptStillRunningUnderPreviousAgentProcess_ReturnsNotYetComplete_AndReleasesNothing()
    {
        if (OperatingSystem.IsWindows()) return;

        using var sleeper = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        var ticket = NewTicket();
        SaveState(ticket, new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, ProcessId = sleeper.Id, ProcessStartedAt = sleeper.StartTime.ToUniversalTime(), CreatedAt = DateTimeOffset.UtcNow });
        var service = NewService();

        try
        {
            var response = service.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

            response.Outcome.ShouldBe(CompleteScriptOutcomeV2.NotYetComplete);
            response.FinalStatus!.State.ShouldBe(ScriptStateV2.Running);
            Directory.Exists(WorkDir(ticket)).ShouldBeTrue("a running script's workspace must not be released");
            new ScriptTombstoneStore(_tombstoneDir).TryRead(ticket.TaskId).ShouldBeNull();
        }
        finally { sleeper.Kill(); }
    }

    [Fact]
    public async Task Complete_WhileStartIsBlockedOnTheIsolationMutex_ReturnsNotYetCompleteWithPending()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var holder = new StartScriptCommandV2 { ScriptTicket = NewTicket(), ScriptBody = "sleep 30", Isolation = ScriptIsolationLevel.FullIsolation, IsolationMutexName = "v2-nyc-mutex", ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(1) };
        var waiter = new StartScriptCommandV2 { ScriptTicket = NewTicket(), ScriptBody = "echo never", Isolation = ScriptIsolationLevel.FullIsolation, IsolationMutexName = "v2-nyc-mutex", ScriptIsolationMutexTimeout = TimeSpan.FromSeconds(20) };
        service.StartScript(holder);
        var blockedStart = Task.Run(() => { try { service.StartScript(waiter); } catch { } });
        await Task.Delay(500);

        var response = service.CompleteScript(new CompleteScriptCommandV2(waiter.ScriptTicket, 0));
        service.CancelScript(new CancelScriptCommandV2(waiter.ScriptTicket, 0));
        service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0));
        await blockedStart;

        response.Outcome.ShouldBe(CompleteScriptOutcomeV2.NotYetComplete);
        response.FinalStatus!.State.ShouldBe(ScriptStateV2.Pending);
    }

    // ── restart after natural exit (#553) ───────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RestartAfterNaturalExit_ReportsCompletedWithRealExitCodeAndLogs(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return;

        var command = NewCommand($"echo tail-marker; exit {exitCode}");
        NewService().StartScript(command);
        WaitUntil(() => PersistedComplete(command.ScriptTicket), "persisted Complete state");

        var status = NewService().GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

        status.State.ShouldBe(ScriptStateV2.Completed);
        status.ExitCode.ShouldBe(exitCode);
        Text(status).ShouldContain("tail-marker");
    }

    [Fact]
    public void PersistedExitCodeMinusOne_IsACompletedResult_NotUnknownTicket()
    {
        var ticket = NewTicket();
        SaveState(ticket, new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Complete, ExitCode = -1, CreatedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow });
        var service = NewService();

        var status = service.GetStatus(new ScriptStatusRequestV2(ticket, 0));
        var complete = service.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

        status.State.ShouldBe(ScriptStateV2.Completed);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult, "a real -1 exit is a result; only the V2 state says whether a ticket is unknown");
        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(-1);
    }

    [Fact]
    public void CompleteAcrossAgentRestart_SecondCompleteIsAlreadyCompleted()
    {
        if (OperatingSystem.IsWindows()) return;

        var command = NewCommand("exit 0");
        var before = NewService();
        before.StartScript(command);
        WaitForCompleted(before, command.ScriptTicket);
        before.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)).Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);

        var afterRestart = NewService();

        afterRestart.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)).Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
        afterRestart.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0)).State.ShouldBe(ScriptStateV2.UnknownTicket, "a released ticket has no status, only a completion tombstone");
    }

    // ── cancel ──────────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_RunningScript_IsIdempotent_AndCompleteStillReportsCanceled()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewCommand("echo started; sleep 60");
        service.StartScript(command);

        var first = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        var second = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        var status = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));
        var complete = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var cancelAfterRelease = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));

        first.State.ShouldBe(ScriptStateV2.Completed);
        first.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        second.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        cancelAfterRelease.State.ShouldBe(ScriptStateV2.Completed, "cancelling a released ticket returns its remembered final status");
        cancelAfterRelease.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    [Fact]
    public void Cancel_FinishedScript_ReturnsItsNaturalResult_NotCanceled()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewCommand("exit 4");
        service.StartScript(command);
        WaitForCompleted(service, command.ScriptTicket);

        var cancel = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));

        cancel.ExitCode.ShouldBe(4);
    }

    // ── V1 untouched ────────────────────────────────────────────────────────

    [Fact]
    public void V1_StillReportsUnknownResultForUnknownTicket_AndRunsScripts()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var v1Command = new StartScriptCommand(NewTicket(), "echo v1; exit 2", ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, Array.Empty<string>(), null, TimeSpan.FromSeconds(10));

        var unknown = service.GetStatus(new ScriptStatusRequest(NewTicket(), 0));
        var started = service.StartScript(v1Command);
        var completed = service.CompleteScript(new CompleteScriptCommand(v1Command.ScriptTicket, 0));

        unknown.State.ShouldBe(ProcessState.Complete);
        unknown.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
        started.State.ShouldBe(ProcessState.Complete);
        completed.ExitCode.ShouldBe(2);
        new ScriptTombstoneStore(_tombstoneDir).TryRead(v1Command.ScriptTicket.TaskId).ShouldBeNull("V1 Complete must not write V2 tombstones");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private LocalScriptService NewService()
    {
        var service = new LocalScriptService(new ScriptStateStoreFactory(), null, new ScriptTombstoneStore(_tombstoneDir));
        _services.Add(service);
        return service;
    }

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket.TaskId);
        return ticket;
    }

    private StartScriptCommandV2 NewCommand(string body) => new() { ScriptTicket = NewTicket(), ScriptBody = body, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    private static string WorkDir(ScriptTicket ticket) => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    private static void SaveState(ScriptTicket ticket, ScriptState state) => new ScriptStateStore(WorkDir(ticket)).Save(state);

    private static bool PersistedComplete(ScriptTicket ticket)
    {
        try { return new ScriptStateStore(WorkDir(ticket)).Load().IsComplete(); }
        catch { return false; }
    }

    private static string Text(ScriptStatusResponseV2 status) => string.Join("\n", status.Logs.Select(l => l.Text));

    private static ScriptStatusResponseV2 WaitForCompleted(LocalScriptService service, ScriptTicket ticket)
    {
        ScriptStatusResponseV2? status = null;

        WaitUntil(() => (status = service.GetStatus(new ScriptStatusRequestV2(ticket, 0))).State == ScriptStateV2.Completed, "V2 state Completed");

        return status!;
    }

    private static void WaitUntil(Func<bool> condition, string signal)
    {
        var sw = Stopwatch.StartNew();

        while (!condition())
        {
            if (sw.Elapsed > WaitLimit)
                throw new TimeoutException($"{signal} was not observed within {WaitLimit.TotalSeconds}s — inspect the scriptstate.json under the temp squid-tentacle-<ticket> dir");

            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        var cleaner = NewService();

        foreach (var id in _tickets)
        {
            try { cleaner.CancelScript(new CancelScriptCommandV2(new ScriptTicket(id), 0)); } catch { }
            try { cleaner.CancelScript(new CancelScriptCommand(new ScriptTicket(id), 0)); } catch { }
            try { if (Directory.Exists(WorkDir(new ScriptTicket(id)))) Directory.Delete(WorkDir(new ScriptTicket(id)), recursive: true); } catch { }
        }

        try { if (Directory.Exists(_tombstoneDir)) Directory.Delete(_tombstoneDir, recursive: true); } catch { }
    }
}
