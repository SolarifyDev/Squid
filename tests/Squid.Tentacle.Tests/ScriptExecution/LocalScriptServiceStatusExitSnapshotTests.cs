using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/> and real bash processes. The only seam is a hook that
/// runs after a status poll has read the log file and before it decides the state; it waits for the script to exit,
/// which pins the window where a poll that saw a running process could report it Completed with logs read before the
/// final lines were written.
/// </summary>
[Trait("Category", "LinuxTentacleScriptServiceV2E2E")]
public class LocalScriptServiceStatusExitSnapshotTests : IDisposable
{
    private const string Script = "echo first-line; sleep 1; echo last-line; exit 7";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PastScriptEnd = TimeSpan.FromSeconds(3);

    private readonly LocalScriptService _service = new();
    private readonly List<ScriptTicket> _tickets = new();

    [Fact]
    public void GetStatusV1_ProcessExitsWhileThePollIsInFlight_NeverReportsCompleteWithoutTheFinalLines()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = NewTicket();
        _service.StartScript(new StartScriptCommand(ticket, Script, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), null, Array.Empty<string>(), null, TimeSpan.Zero));
        WaitUntil(() => Text(_service.GetStatus(new ScriptStatusRequest(ticket, 0)).Logs).Contains("first-line"), "first-line logged");

        _service.StatusLogsReadHookForTests = OneShotWaitPastScriptEnd();
        var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));

        (status.State == ProcessState.Running || Text(status.Logs).Contains("last-line")).ShouldBeTrue($"a poll reported {status.State} (exit {status.ExitCode}) with logs read before the script finished writing: [{Text(status.Logs)}]");
    }

    [Fact]
    public void GetStatusV2_ProcessExitsWhileThePollIsInFlight_NeverReportsCompletedWithoutTheFinalLines()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = NewTicket();
        _service.StartScript(new StartScriptCommandV2 { ScriptTicket = ticket, ScriptBody = Script, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) });
        WaitUntil(() => Text(_service.GetStatus(new ScriptStatusRequestV2(ticket, 0)).Logs).Contains("first-line"), "first-line logged");

        _service.StatusLogsReadHookForTests = OneShotWaitPastScriptEnd();
        var status = _service.GetStatus(new ScriptStatusRequestV2(ticket, 0));

        (status.State == ScriptStateV2.Running || Text(status.Logs).Contains("last-line")).ShouldBeTrue($"a poll reported {status.State} (exit {status.ExitCode}) with logs read before the script finished writing: [{Text(status.Logs)}]");
    }

    private Action OneShotWaitPastScriptEnd() => () =>
    {
        _service.StatusLogsReadHookForTests = null;
        Thread.Sleep(PastScriptEnd);
    };

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket);
        return ticket;
    }

    private static string Text(IEnumerable<ProcessOutput> logs) => string.Join("\n", logs.Select(l => l.Text));

    private static void WaitUntil(Func<bool> condition, string signal)
    {
        var sw = Stopwatch.StartNew();

        while (!condition())
        {
            if (sw.Elapsed > WaitLimit)
                throw new TimeoutException($"{signal} was not observed within {WaitLimit.TotalSeconds}s — inspect output.log under the temp squid-tentacle-<ticket> dir");

            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        _service.StatusLogsReadHookForTests = null;

        foreach (var ticket in _tickets)
        {
            try { _service.CancelScript(new CancelScriptCommand(ticket, 0)); } catch { }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}"), recursive: true); } catch { }
        }
    }
}
