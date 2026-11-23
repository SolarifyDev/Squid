using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/>, real bash processes, real state files.
/// The drain runs after Halibut polling is cancelled, so no CompleteScript/CancelScript RPC ever arrives:
/// it must key off the script PROCESSES exiting, not the registry emptying.
/// </summary>
[Trait("Category", "LinuxTentacleScriptExitPersistenceE2E")]
public class LocalScriptServiceDrainTests : IDisposable
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PromptLimit = TimeSpan.FromSeconds(15);

    private readonly LocalScriptService _service = new();
    private readonly List<string> _tickets = new();

    [Fact]
    public async Task Drain_AllScriptsAlreadyExited_ReturnsPromptlyWithoutCompleteScript()
    {
        if (OperatingSystem.IsWindows()) return;

        var first = Start("exit 0");
        var second = Start("exit 3");
        WaitForPersistedComplete(first);
        WaitForPersistedComplete(second);

        var elapsed = await TimeDrainAsync(LongTimeout);

        elapsed.ShouldBeLessThan(PromptLimit, "exited scripts stay tracked until the server completes them, but must not hold shutdown for the drain timeout");
    }

    [Fact]
    public async Task Drain_ScriptExitsDuringDrain_ReturnsEarlyAndPersistsRealExitCode()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = Start("sleep 2; exit 9");

        var elapsed = await TimeDrainAsync(LongTimeout);

        elapsed.ShouldBeLessThan(PromptLimit);
        var status = new LocalScriptService().GetStatus(new ScriptStatusRequest(ticket, 0));
        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(9, "the script finished inside the drain window; a restarted agent must report its real exit code");
    }

    [Fact]
    public async Task Drain_LongRunningScript_IsKilledAtTimeout_AndStaysUnknownResult()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = Start("sleep 60");
        _service.GetStatus(new ScriptStatusRequest(ticket, 0)).State.ShouldBe(ProcessState.Running);

        var elapsed = await TimeDrainAsync(TimeSpan.FromSeconds(1));

        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900), "a running script must be waited for until the timeout");
        elapsed.ShouldBeLessThan(PromptLimit);
        Thread.Sleep(500);
        var status = new LocalScriptService().GetStatus(new ScriptStatusRequest(ticket, 0));
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult, "a drain-timeout kill is not a script result; the kill signal's exit code must not be persisted");
    }

    [Fact]
    public async Task Drain_ScriptDeclaredDetached_IsNeitherWaitedForNorKilled()
    {
        if (OperatingSystem.IsWindows()) return;

        // Same shape as the upgrade script: marks itself detached, then outlives the agent restart it triggers.
        var ticket = Start($"touch \"$PWD/{LocalScriptService.DrainExemptMarkerFileName}\"; sleep 30");
        WaitForFile(Path.Combine(WorkDir(ticket), LocalScriptService.DrainExemptMarkerFileName));

        var elapsed = await TimeDrainAsync(TimeSpan.FromSeconds(3));

        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "a detached script must not hold the drain (it waits for the restart the drain is blocking)");
        _service.GetStatus(new ScriptStatusRequest(ticket, 0)).State.ShouldBe(ProcessState.Running, "and must not be killed either: its process tree includes the scope the upgrade runs in");
    }

    [Fact]
    public async Task Drain_DetachedScriptDoesNotExcuseOtherRunningScripts()
    {
        if (OperatingSystem.IsWindows()) return;

        var detached = Start($"touch \"$PWD/{LocalScriptService.DrainExemptMarkerFileName}\"; sleep 30");
        WaitForFile(Path.Combine(WorkDir(detached), LocalScriptService.DrainExemptMarkerFileName));
        Start("sleep 30");

        var elapsed = await TimeDrainAsync(TimeSpan.FromSeconds(1));

        elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900), "an ordinary running script must still be waited for");
    }

    private async Task<TimeSpan> TimeDrainAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();

        await _service.WaitForDrainAsync(timeout);

        return sw.Elapsed;
    }

    private ScriptTicket Start(string body)
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket.TaskId);
        _service.StartScript(new StartScriptCommand(ticket, body, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), null, Array.Empty<string>(), null, TimeSpan.Zero));

        return ticket;
    }

    private static string WorkDir(ScriptTicket ticket) => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    private static void WaitForFile(string path)
    {
        var sw = Stopwatch.StartNew();

        while (!File.Exists(path))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"{path} not created within 20s");

            Thread.Sleep(5);
        }
    }

    private static void WaitForPersistedComplete(ScriptTicket ticket)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try { if (new ScriptStateStore(WorkDir(ticket)).Load().IsComplete()) return; } catch { }

            Thread.Sleep(5);
        }

        throw new TimeoutException($"persisted Complete state for {ticket.TaskId} not observed within 20s");
    }

    public void Dispose()
    {
        var cleaner = new LocalScriptService();

        foreach (var id in _tickets)
        {
            var ticket = new ScriptTicket(id);

            try { cleaner.CancelScript(new CancelScriptCommand(ticket, 0)); } catch { }
            try { if (Directory.Exists(WorkDir(ticket))) Directory.Delete(WorkDir(ticket), recursive: true); } catch { }
        }
    }
}
