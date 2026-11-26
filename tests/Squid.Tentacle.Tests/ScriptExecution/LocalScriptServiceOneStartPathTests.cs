using System.Diagnostics;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Security.Admission;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/>, real bash processes, real state files. The races are forced
/// deterministically through the service's test hooks (no sleeps): a call lands at an exact point of a start that is underway.
/// V1 and V2 Start share one start path (one claim per ticket under its lock), and a Cancel that meets a start is handed to the
/// starter, which carries it out once the script is tracked.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class LocalScriptServiceOneStartPathTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-one-start-{Guid.NewGuid():N}");
    private readonly List<ScriptTicket> _tickets = new();
    private readonly List<LocalScriptService> _services = new();

    public LocalScriptServiceOneStartPathTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void V1Cancel_LandingBetweenFilesWrittenAndTracking_KillsTheProcess_AndKeepsTheStartersState()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var pidFile = Path.Combine(_root, "pid");
        var command = NewV1Command($"echo $$ > '{pidFile}'; exec sleep 300");
        ScriptStatusResponse? cancelAnswer = null;
        var stateKept = false;

        service.AfterFilesWrittenHookForTests = Once(() =>
        {
            cancelAnswer = service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));
            stateKept = new ScriptStateStore(WorkDir(command.ScriptTicket)).Exists();
        });

        var started = service.StartScript(command);

        cancelAnswer.ShouldNotBeNull("the cancel must land inside the start, between its file writes and its process start");
        cancelAnswer.State.ShouldBe(ProcessState.Complete);
        cancelAnswer.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        stateKept.ShouldBeTrue("a Cancel meeting a start must not delete the Starting state the starter is about to use");
        started.State.ShouldBe(ProcessState.Complete, "the starter must carry out the cancel handed to it instead of reporting the script Running");
        started.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        service.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse("the cancelled script is no longer tracked");
        ProcessIsAlive(pidFile).ShouldBeFalse("a cancel that met the start must kill the process; check `ps` for a `sleep 300` left behind");
    }

    [Fact]
    public void V2Cancel_LandingBetweenFilesWrittenAndTracking_KillsTheProcess()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var pidFile = Path.Combine(_root, "pid");
        var command = NewV2Command($"echo $$ > '{pidFile}'; exec sleep 300");
        ScriptStatusResponseV2? cancelAnswer = null;

        service.AfterFilesWrittenHookForTests = Once(() => cancelAnswer = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0)));

        var started = service.StartScript(command);

        cancelAnswer.ShouldNotBeNull("the cancel must land inside the start, between its file writes and its process start");
        cancelAnswer.State.ShouldBe(ScriptStateV2.Pending, "a V2 Cancel meeting a start answers Pending, as before");
        started.State.ShouldBe(ScriptStateV2.Completed, "the starter must carry out the cancel handed to it instead of reporting the script Running");
        started.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        ProcessIsAlive(pidFile).ShouldBeFalse("a cancel that met the start must kill the process; check `ps` for a `sleep 300` left behind");

        var completed = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));

        completed.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        completed.FinalStatus!.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    [Theory]
    [InlineData(StartProtocol.V1, StartProtocol.V1)]
    [InlineData(StartProtocol.V1, StartProtocol.V2)]
    [InlineData(StartProtocol.V2, StartProtocol.V1)]
    [InlineData(StartProtocol.V2, StartProtocol.V2)]
    public void SecondStart_OfATicketWhoseStartIsUnderway_DoesNotLaunchItAgain(StartProtocol first, StartProtocol second)
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var marker = Path.Combine(_root, "runs");
        var ticket = NewTicket();
        var body = $"echo run >> '{marker}'";
        bool? secondIsTerminal = null;

        service.StartUnderwayHookForTests = Once(() => secondIsTerminal = StartIsTerminal(service, second, ticket, body));

        StartIsTerminal(service, first, ticket, body);

        secondIsTerminal.ShouldBe(false, "the second Start must land while the first is underway and answer non-terminal (the script is being started)");
        WaitUntil(() => service.GetStatus(new ScriptStatusRequest(ticket, 0)).State == ProcessState.Complete, "the started script never completed; check its workspace output.log");
        File.ReadAllLines(marker).Length.ShouldBe(1, "one ticket must run once, whichever protocol its two Starts used");
    }

    [Fact]
    public void V1GetStatus_WhileTheStartIsUnderway_ReportsRunning_NotUnknownTicket()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewV1Command("exit 0");
        ScriptStatusResponse? status = null;

        service.StartUnderwayHookForTests = Once(() => status = service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 3)));

        service.StartScript(command);

        status.ShouldNotBeNull();
        status.State.ShouldBe(ProcessState.Running, "Complete + UnknownResult reads as an unknown ticket to a V1 server, which re-dispatches the script");
        status.Logs.ShouldBeEmpty();
        status.NextLogSequence.ShouldBe(3, "nothing new to deliver: the cursor stays where the server is");
    }

    [Fact]
    public void V1Complete_WhileTheStartIsUnderway_KeepsTheStartersState_AndTheScriptStillRuns()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewV1Command("echo done");
        ScriptStatusResponse? completeAnswer = null;
        var stateKept = false;

        service.AfterFilesWrittenHookForTests = Once(() =>
        {
            completeAnswer = service.CompleteScript(new CompleteScriptCommand(command.ScriptTicket, 0));
            stateKept = new ScriptStateStore(WorkDir(command.ScriptTicket)).Exists();
        });

        service.StartScript(command);

        completeAnswer.ShouldNotBeNull();
        completeAnswer.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult, "the V1 answer for a ticket with nothing to release is unchanged");
        stateKept.ShouldBeTrue("a Complete meeting a start must not delete the Starting state the starter is about to use");

        WaitUntil(() => service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0)).State == ProcessState.Complete, "the started script never completed");

        var completed = service.CompleteScript(new CompleteScriptCommand(command.ScriptTicket, 0));

        completed.ExitCode.ShouldBe(0);
        completed.Logs.Select(l => l.Text).ShouldContain("done");
    }

    [Fact]
    public void IsRunningScript_WhileTheStartIsUnderway_IsTrue()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var command = NewV1Command("exit 0");
        bool? running = null;

        service.StartUnderwayHookForTests = Once(() => running = service.IsRunningScript(command.ScriptTicket.TaskId));

        service.StartScript(command);

        running.ShouldBe(true, "a script being started is running: disk self-heal must not reclaim the workspace it is writing");
    }

    [Fact]
    public void V1Cancel_StartFinishesBeforeTheCancelRecordsItsRequest_TheCancelKillsTheTrackedScript()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService();
        var pidFile = Path.Combine(_root, "pid");
        var command = NewV1Command($"echo $$ > '{pidFile}'; exec sleep 300");
        using var underway = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hookFired = false;

        service.StartUnderwayHookForTests = Once(() =>
        {
            underway.Set();
            release.Wait(WaitLimit).ShouldBeTrue("the test never released the start");
        });

        var start = Task.Run(() => service.StartScript(command));
        SpinWait.SpinUntil(() => underway.IsSet || start.IsCompleted, WaitLimit);
        if (start.IsFaulted) throw start.Exception!;
        underway.IsSet.ShouldBeTrue("the start never got underway");

        // The Cancel saw the start in flight; before it records its request the start tracks its script and ends its claim.
        service.BeforeCancelRequestRecordedHookForTests = Once(() =>
        {
            hookFired = true;
            release.Set();
            start.Wait(WaitLimit).ShouldBeTrue("the released start never finished");
            WaitUntil(() => File.Exists(pidFile), "the started script never wrote its pid");
        });

        var cancelled = service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));

        hookFired.ShouldBeTrue("the cancel must meet the start while it is still in flight");
        start.Result.State.ShouldBe(ProcessState.Running, "the start ended before the cancel was recorded, so it had nothing to carry out");
        cancelled.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        WaitUntil(() => !ProcessIsAlive(pidFile), "a cancel recorded after the start ended must cancel the tracked script itself; check `ps` for a `sleep 300` left behind");
    }

    [Fact]
    public void V1DuplicateStart_ScriptExitsAfterThePreLockDrain_ReportsRunningWithoutDrainingUnderTheLock()
    {
        if (OperatingSystem.IsWindows()) return;

        var service = NewService(outputDrainTimeout: TimeSpan.FromSeconds(30));
        var fifo = Path.Combine(_root, "go");
        var pidFile = Path.Combine(_root, "pid");
        var childPidFile = Path.Combine(_root, "child");
        RunChecked("mkfifo", fifo);

        // The child keeps the script's stdout open after the script exits, so its output never reaches EOF by itself.
        var command = NewV1Command($"sleep 300 & echo $! > '{childPidFile}'; echo $$ > '{pidFile}'; read _ < '{fifo}'; exit 3");
        service.StartScript(command);
        WaitUntil(() => File.Exists(pidFile), "the script never wrote its pid");

        // The duplicate Start drains while the script still runs (a no-op); the script then exits before the ticket lock is taken.
        service.AfterPreLockDrainHookForTests = Once(() =>
        {
            File.WriteAllText(fifo, "go\n");
            WaitUntil(() => !ProcessIsAlive(pidFile), "the script did not exit after its fifo was written");
        });

        var sw = Stopwatch.StartNew();
        var duplicate = service.StartScript(command);

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the duplicate Start must not drain a held output pipe under the ticket lock (drain budget 30s)");
        duplicate.State.ShouldBe(ProcessState.Running, "without the drain the output may be incomplete, so the script is not reported finished yet");

        // Close the held pipe so cleanup does not wait out the drain budget.
        KillLeftover(childPidFile);
    }

    [Fact]
    public void V2Start_RejectedByAdmission_EndsItsClaim()
    {
        if (OperatingSystem.IsWindows()) return;

        var policy = new AdmissionPolicy { Rules = { new AdmissionRule { Id = "deny-all", DenyScriptBodyRegex = { ".*" } } } };
        var service = new LocalScriptService(new ScriptStateStoreFactory(), new FixedAdmissionSource(policy));
        var command = NewV2Command("echo denied");

        var started = service.StartScript(command);

        started.State.ShouldBe(ScriptStateV2.Completed);
        started.ExitCode.ShouldBe(-403);
        service.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse("a rejected start must end its claim");
        service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0)).State.ShouldNotBe(ScriptStateV2.Pending, "a rejected start left in flight reads as Pending forever");
    }

    public enum StartProtocol { V1, V2 }

    private static bool StartIsTerminal(LocalScriptService service, StartProtocol protocol, ScriptTicket ticket, string body)
    {
        if (protocol == StartProtocol.V1)
            return service.StartScript(new StartScriptCommand(ticket, body, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, Array.Empty<string>(), null, TimeSpan.Zero)).State == ProcessState.Complete;

        return service.StartScript(new StartScriptCommandV2 { ScriptTicket = ticket, ScriptBody = body, Isolation = ScriptIsolationLevel.NoIsolation }).State is ScriptStateV2.Completed;
    }

    private static Action Once(Action action)
    {
        var fired = 0;

        return () => { if (Interlocked.Exchange(ref fired, 1) == 0) action(); };
    }

    private LocalScriptService NewService(TimeSpan? outputDrainTimeout = null)
    {
        var service = new LocalScriptService { OutputDrainTimeout = outputDrainTimeout ?? LocalScriptService.DefaultOutputDrainTimeout };
        _services.Add(service);
        return service;
    }

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket);
        return ticket;
    }

    private StartScriptCommand NewV1Command(string body) => new(NewTicket(), body, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(1), null, Array.Empty<string>(), null, TimeSpan.Zero);

    private StartScriptCommandV2 NewV2Command(string body) => new() { ScriptTicket = NewTicket(), ScriptBody = body, Isolation = ScriptIsolationLevel.NoIsolation };

    private static string WorkDir(ScriptTicket ticket) => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    // No pid file means the script was killed before it ran its first line.
    private static bool ProcessIsAlive(string pidFile)
    {
        if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void RunChecked(string file, string argument)
    {
        using var process = Process.Start(new ProcessStartInfo(file, argument) { UseShellExecute = false })!;

        process.WaitForExit(WaitLimit).ShouldBeTrue($"{file} {argument} did not finish");
        process.ExitCode.ShouldBe(0, $"{file} {argument} failed");
    }

    private sealed class FixedAdmissionSource(AdmissionPolicy policy) : IAdmissionPolicySource
    {
        public AdmissionPolicy Current { get; } = policy;

        public event Action<AdmissionPolicy>? Updated { add { } remove { } }
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var sw = Stopwatch.StartNew();

        while (!condition())
        {
            if (sw.Elapsed > WaitLimit) throw new TimeoutException($"{failure} (waited {WaitLimit.TotalSeconds}s)");

            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        KillLeftover(Path.Combine(_root, "child"));

        foreach (var ticket in _tickets)
        {
            foreach (var service in _services)
                try { service.CancelScript(new CancelScriptCommand(ticket, 0)); } catch { }

            try { if (Directory.Exists(WorkDir(ticket))) Directory.Delete(WorkDir(ticket), recursive: true); } catch { }
        }

        KillLeftover(Path.Combine(_root, "pid"));

        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A failing run (the cancel was lost) leaves its `sleep 300` behind: never leak it to the next test.
    private static void KillLeftover(string pidFile)
    {
        if (!ProcessIsAlive(pidFile)) return;

        try
        {
            using var process = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
