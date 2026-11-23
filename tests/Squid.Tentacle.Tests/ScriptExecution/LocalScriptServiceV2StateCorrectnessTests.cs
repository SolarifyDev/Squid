using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/>, real bash, real state files and tombstone files. A
/// "restart" is a new service instance over the same temp workspace and tombstone directory.
/// </summary>
[Trait("Category", "LinuxTentacleScriptServiceV2E2E")]
public class LocalScriptServiceV2StateCorrectnessTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly string _tombstoneDir = Path.Combine(Path.GetTempPath(), $"squid-v2-state-tombstones-{Guid.NewGuid():N}");
    private readonly string _blockedTombstoneParent = Path.Combine(Path.GetTempPath(), $"squid-v2-blocked-{Guid.NewGuid():N}");
    private readonly List<string> _tickets = new();
    private readonly List<LocalScriptService> _services = new();

    [Fact]
    public void GetStatus_WhileStartIsBlockedOnIsolationMutex_ReportsPending_NotUnknownTicket()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var holder = NewCommand("sleep 30", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(1));
        var waiter = NewCommand("echo never", ScriptIsolationLevel.FullIsolation, TimeSpan.FromSeconds(6));
        service.StartScript(holder);

        var blockedStart = Task.Run(() => service.StartScript(waiter));
        Thread.Sleep(600);
        var status = service.GetStatus(new ScriptStatusRequestV2(waiter.ScriptTicket, 0));

        status.State.ShouldBe(ScriptStateV2.Pending, "a Start that is still waiting for the mutex has no state file yet; UnknownTicket makes the server re-dispatch the script");

        service.CancelScript(new CancelScriptCommandV2(waiter.ScriptTicket, 0));
        service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0));
        Should.Throw<AggregateException>(() => blockedStart.Wait(TimeSpan.FromSeconds(20)), "the cancelled waiter's Start unwinds with a cancellation");
    }

    [Fact]
    public void Start_AfterCompleteReleasedTheTicket_ReportsTheRecordedResult_AndNeverRunsTheScriptAgain()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var marker = Path.Combine(Path.GetTempPath(), $"squid-v2-rerun-{Guid.NewGuid():N}");
        var command = NewCommand($"echo run >> {marker}; exit 5");
        service.StartScript(command);
        WaitForCompleted(service, command.ScriptTicket);
        service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)).Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);

        var delayedDuplicate = service.StartScript(command);
        var afterRestart = NewService().StartScript(command);
        Thread.Sleep(500);

        delayedDuplicate.State.ShouldBe(ScriptStateV2.Completed);
        delayedDuplicate.ExitCode.ShouldBe(5);
        afterRestart.State.ShouldBe(ScriptStateV2.Completed);
        File.ReadAllLines(marker).Length.ShouldBe(1, "a Start after Complete must not run the finished script a second time");
        File.Delete(marker);
    }

    [Fact]
    public void Complete_TombstoneCannotBeWritten_KeepsTheStateFile_SoACrashLaterStillSeesTheResult()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewServiceWithUnwritableTombstones();
        var command = NewCommand("echo out; exit 7");
        service.StartScript(command);
        WaitForCompleted(service, command.ScriptTicket);

        var complete = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var afterRestart = NewServiceWithUnwritableTombstones().GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(7);
        afterRestart.State.ShouldBe(ScriptStateV2.Completed, "without a tombstone the persisted state is the only record; releasing it makes a restart report UnknownTicket and the script re-runs");
        afterRestart.ExitCode.ShouldBe(7);
    }

    [Fact]
    public void CompleteOfPersistedResult_TombstoneCannotBeWritten_KeepsTheStateFile()
    {
        if (OperatingSystem.IsWindows()) return;

        var abandoned = NewService();
        var command = NewCommand("exit 4");
        abandoned.StartScript(command);
        WaitUntil(() => PersistedComplete(command.ScriptTicket));

        var restarted = NewServiceWithUnwritableTombstones();
        var first = restarted.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var second = restarted.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));

        first.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        second.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed, "the state was kept, so a repeated Complete is still answered with the recorded result");
        second.FinalStatus!.ExitCode.ShouldBe(4);
    }

    private LocalScriptService NewService()
    {
        var service = new LocalScriptService(new ScriptStateStoreFactory(), null, new ScriptTombstoneStore(_tombstoneDir));
        _services.Add(service);
        return service;
    }

    private LocalScriptService NewServiceWithUnwritableTombstones()
    {
        // A regular file where the directory must be makes CreateDirectory (and so every tombstone write) fail.
        Directory.CreateDirectory(_blockedTombstoneParent);
        var blocker = Path.Combine(_blockedTombstoneParent, "not-a-directory");
        if (!File.Exists(blocker)) File.WriteAllText(blocker, "x");

        var service = new LocalScriptService(new ScriptStateStoreFactory(), null, new ScriptTombstoneStore(Path.Combine(blocker, "tombstones")));
        _services.Add(service);
        return service;
    }

    private StartScriptCommandV2 NewCommand(string body, ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, TimeSpan? mutexTimeout = null)
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket.TaskId);

        return new StartScriptCommandV2 { ScriptTicket = ticket, ScriptBody = body, Isolation = isolation, IsolationMutexName = "v2-state-correctness-" + _tombstoneDir.GetHashCode(), ScriptIsolationMutexTimeout = mutexTimeout ?? TimeSpan.FromMinutes(5) };
    }

    private static string WorkDir(ScriptTicket ticket) => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    private static bool PersistedComplete(ScriptTicket ticket)
    {
        try { return new ScriptStateStore(WorkDir(ticket)).Load().IsComplete(); }
        catch { return false; }
    }

    private static ScriptStatusResponseV2 WaitForCompleted(LocalScriptService service, ScriptTicket ticket)
    {
        ScriptStatusResponseV2? status = null;

        WaitUntil(() => (status = service.GetStatus(new ScriptStatusRequestV2(ticket, 0))).State == ScriptStateV2.Completed);

        return status!;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();

        while (!condition())
        {
            if (sw.Elapsed > WaitLimit) throw new TimeoutException("expected condition was not observed");

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
        try { if (Directory.Exists(_blockedTombstoneParent)) Directory.Delete(_blockedTombstoneParent, recursive: true); } catch { }
    }
}
