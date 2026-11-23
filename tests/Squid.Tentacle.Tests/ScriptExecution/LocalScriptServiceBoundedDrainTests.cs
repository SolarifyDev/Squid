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
using Squid.Tentacle.ScriptExecution.Logging;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/> and real bash. The scripts leave a background child that
/// inherits stdout (<c>sleep 8 &amp;</c> without redirect), the shape of <c>nohup app &amp;</c>: the shell exits at once but the pipe
/// stays open for the child's lifetime. Parameterless <c>Process.WaitForExit()</c> blocks for that whole lifetime.
/// </summary>
[Trait("Category", "LinuxTentacleScriptExitPersistenceE2E")]
public class LocalScriptServiceBoundedDrainTests : IDisposable
{
    private const string LeakyScript = "echo own-output; sleep 0.4; sleep 8 & exit 3";
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MustReturnWithin = TimeSpan.FromSeconds(5);

    private readonly string _tombstoneDir = Path.Combine(Path.GetTempPath(), $"squid-drain-tombstones-{Guid.NewGuid():N}");
    private readonly List<string> _tickets = new();
    private readonly List<LocalScriptService> _services = new();

    [Fact]
    public void V1Complete_BackgroundChildHoldsStdout_ReturnsWithinBoundWithOwnOutputAndTruncationNote()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var ticket = StartV1(service, LeakyScript);

        var (status, elapsed) = Timed(() => service.CompleteScript(new CompleteScriptCommand(ticket, 0)));

        elapsed.ShouldBeLessThan(MustReturnWithin, "Complete must not wait for the background child to release the pipe");
        status.ExitCode.ShouldBe(3);
        Text(status.Logs).ShouldContain("own-output");
        Text(status.Logs).ShouldContain("output truncated");
    }

    [Fact]
    public void V1GetStatus_BackgroundChildHoldsStdout_ReportsCompleteWithinBound()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var ticket = StartV1(service, LeakyScript);
        Thread.Sleep(900);

        var (status, elapsed) = Timed(() => service.GetStatus(new ScriptStatusRequest(ticket, 0)));

        elapsed.ShouldBeLessThan(MustReturnWithin);
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(3);
        Text(status.Logs).ShouldContain("own-output");
    }

    [Fact]
    public void NaturalExitPersistence_BackgroundChildHoldsStdout_RecordsCompleteWithinBound()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var ticket = StartV1(service, LeakyScript);
        var sw = Stopwatch.StartNew();

        WaitUntil(() => IsPersistedComplete(ticket), TimeSpan.FromSeconds(20));

        sw.Elapsed.ShouldBeLessThan(MustReturnWithin, "the exit callback must not wait for the background child either");
        Text(new SequencedLogReader(Path.Combine(WorkDir(ticket), "output.log")).ReadFrom(-1).Select(e => new ProcessOutput(ProcessOutputSource.StdOut, e.Text)).ToList()).ShouldContain("own-output");
    }

    [Fact]
    public async Task Drain_ExitedScriptWithBackgroundChild_DoesNotBlockShutdownUntilChildExits()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        StartV1(service, LeakyScript);
        Thread.Sleep(900);

        var sw = Stopwatch.StartNew();
        await service.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4.5), "shutdown must not be held until the background child exits");
    }

    [Fact]
    public async Task Drain_ExitedScriptWithShortLivedChild_RecordsCompleteOnlyAfterItsTailOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService(TimeSpan.FromSeconds(5));
        var ticket = StartV1(service, "echo head; sleep 0.3; (sleep 1.5; echo TAIL-MARKER) & exit 0");
        Thread.Sleep(900);

        await service.WaitForDrainAsync(TimeSpan.FromSeconds(1));

        IsPersistedComplete(ticket).ShouldBeTrue();
        Text(new SequencedLogReader(Path.Combine(WorkDir(ticket), "output.log")).ReadFrom(-1).Select(e => new ProcessOutput(ProcessOutputSource.StdOut, e.Text)).ToList()).ShouldContain("TAIL-MARKER", customMessage: "the shutdown drain must not record Complete before the final output the script produced");
    }

    [Fact]
    public void V2Complete_BackgroundChildHoldsStdout_DoesNotBlockOtherTicketsInTheSameLockSlot()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService(TimeSpan.FromSeconds(4));
        var leaky = NewTicket();
        service.StartScript(new StartScriptCommandV2 { ScriptTicket = leaky, ScriptBody = LeakyScript, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(1) });
        Thread.Sleep(900);
        var sibling = TicketInSameLockSlotAs(leaky);

        var completing = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(leaky, 0)));
        Thread.Sleep(500);
        var (_, elapsed) = Timed(() => service.CompleteScript(new CompleteScriptCommandV2(sibling, 0)));

        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5), "an unrelated ticket sharing the lock stripe must not wait for the leaky script's output pipe");
        completing.Wait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
    }

    [Fact]
    public void V2CompleteAndCancel_BackgroundChildHoldsStdout_ReturnWithinBound()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var completed = NewTicket();
        var cancelled = NewTicket();
        service.StartScript(new StartScriptCommandV2 { ScriptTicket = completed, ScriptBody = LeakyScript, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(1) });
        service.StartScript(new StartScriptCommandV2 { ScriptTicket = cancelled, ScriptBody = "echo own-output; (sleep 8 &); sleep 60", ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(1) });
        Thread.Sleep(900);

        var (complete, completeElapsed) = Timed(() => service.CompleteScript(new CompleteScriptCommandV2(completed, 0)));
        var (cancel, cancelElapsed) = Timed(() => service.CancelScript(new CancelScriptCommandV2(cancelled, 0)));

        completeElapsed.ShouldBeLessThan(MustReturnWithin);
        complete.FinalStatus!.ExitCode.ShouldBe(3);
        Text(complete.FinalStatus!.Logs).ShouldContain("own-output");
        cancelElapsed.ShouldBeLessThan(MustReturnWithin);
        cancel.State.ShouldBe(ScriptStateV2.Completed);
    }

    private static string Text(IEnumerable<ProcessOutput> logs) => string.Join("\n", logs.Select(l => l.Text));

    private LocalScriptService NewService(TimeSpan? bound = null)
    {
        var service = new LocalScriptService(new ScriptStateStoreFactory(), null, new ScriptTombstoneStore(_tombstoneDir)) { OutputDrainTimeout = bound ?? Bound };
        _services.Add(service);
        return service;
    }

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket.TaskId);
        return ticket;
    }

    private ScriptTicket TicketInSameLockSlotAs(ScriptTicket ticket)
    {
        var stripes = new TicketLockStripes();

        while (true)
        {
            var candidate = NewTicket();

            if (ReferenceEquals(stripes.For(candidate.TaskId), stripes.For(ticket.TaskId)) && candidate.TaskId != ticket.TaskId) return candidate;
        }
    }

    private ScriptTicket StartV1(LocalScriptService service, string body)
    {
        var ticket = NewTicket();
        service.StartScript(new StartScriptCommand(ticket, body, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), null, Array.Empty<string>(), null, TimeSpan.Zero));

        return ticket;
    }

    private static (T Result, TimeSpan Elapsed) Timed<T>(Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        var result = action();

        return (result, sw.Elapsed);
    }

    private static string WorkDir(ScriptTicket ticket) => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    private static bool IsPersistedComplete(ScriptTicket ticket)
    {
        try { return new ScriptStateStore(WorkDir(ticket)).Load().IsComplete(); }
        catch { return false; }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();

        while (!condition())
        {
            if (sw.Elapsed > limit) throw new TimeoutException("persisted Complete state was not observed");

            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        var cleaner = NewService();

        foreach (var id in _tickets)
        {
            try { cleaner.CancelScript(new CancelScriptCommand(new ScriptTicket(id), 0)); } catch { }
            try { if (Directory.Exists(WorkDir(new ScriptTicket(id)))) Directory.Delete(WorkDir(new ScriptTicket(id)), recursive: true); } catch { }
        }

        try { if (Directory.Exists(_tombstoneDir)) Directory.Delete(_tombstoneDir, recursive: true); } catch { }
    }
}
