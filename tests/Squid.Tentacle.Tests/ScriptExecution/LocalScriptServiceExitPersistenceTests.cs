using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.Logging;
using Squid.Tentacle.ScriptExecution.State;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: HIGH — real <see cref="LocalScriptService"/>, real bash processes, real state files in the
/// real temp workspace. A "restart" is a brand-new service instance over the same workspace with the first
/// instance abandoned (never CompleteScript'd), which is exactly what the new instance sees after a crash.
/// </summary>
[Trait("Category", "LinuxTentacleScriptExitPersistenceE2E")]
public class LocalScriptServiceExitPersistenceTests : IDisposable
{
    private const string FinalMarker = "FINAL-LINE-MARKER";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly List<string> _tickets = new();
    private readonly List<LocalScriptService> _abandonedServices = new();

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RestartAfterScriptExited_GetStatus_ReturnsRealExitCodeAndFullLogs(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = StartScript(Abandoned(), $"echo first-line; echo {FinalMarker}; exit {exitCode}");
        WaitForPersistedComplete(ticket);

        var status = new LocalScriptService().GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(exitCode, "a script that finished while the agent was down must keep its real exit code, not UnknownResult");
        AllText(status).ShouldContain("first-line");
        AllText(status).ShouldContain(FinalMarker);
    }

    [Fact]
    public void RestartAfterScriptExited_RedeliveredStartScript_ReturnsCompleteWithRealExitCode()
    {
        if (OperatingSystem.IsWindows()) return;

        var command = NewCommand("echo hi; exit 5");
        Abandoned().StartScript(command);
        _tickets.Add(command.ScriptTicket.TaskId);
        WaitForPersistedComplete(command.ScriptTicket);

        var status = new LocalScriptService().StartScript(command);

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(5);
    }

    [Fact]
    public void RestartAfterScriptExited_CompleteScript_ReturnsRealExitCodeAndLogs_ThenConsumesState()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = StartScript(Abandoned(), $"echo {FinalMarker}; exit 3");
        WaitForPersistedComplete(ticket);

        var status = new LocalScriptService().CompleteScript(new CompleteScriptCommand(ticket, 0));

        status.ExitCode.ShouldBe(3);
        AllText(status).ShouldContain(FinalMarker);
        new ScriptStateStore(WorkDir(ticket)).Exists().ShouldBeFalse("the recorded state is consumed once the server completes the script");
    }

    [Fact]
    public void RestartWithRunningStateAndDeadPid_StillReportsUnknownResult()
    {
        var ticket = NewTicket();
        SaveState(ticket, new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, ProcessId = int.MaxValue, CreatedAt = DateTimeOffset.UtcNow });

        var status = new LocalScriptService().GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void CompleteScript_RunningStateWithDeadPid_StillReportsUnknownResult()
    {
        var ticket = NewTicket();
        SaveState(ticket, new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, ProcessId = int.MaxValue, CreatedAt = DateTimeOffset.UtcNow });

        var status = new LocalScriptService().CompleteScript(new CompleteScriptCommand(ticket, 0));

        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void StateFile_OldFormatWithoutNewerFields_IsReadCorrectly()
    {
        var workDir = Directory.CreateTempSubdirectory("squid-state-compat-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(workDir, "scriptstate.json"), """{"ticketId":"t1","progress":2,"createdAt":"2025-01-01T00:00:00+00:00","processId":42}""");

            var state = new ScriptStateStore(workDir).Load();

            state.Progress.ShouldBe(ScriptProgress.Running);
            state.ProcessId.ShouldBe(42);
            state.ExitCode.ShouldBeNull();
            state.CompletedAt.ShouldBeNull();
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void StateFile_NewerWriterWithUnknownFields_IsReadByThisReader()
    {
        var workDir = Directory.CreateTempSubdirectory("squid-state-fwd-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(workDir, "scriptstate.json"), """{"ticketId":"t1","progress":3,"exitCode":9,"someFutureField":{"a":1}}""");

            var state = new ScriptStateStore(workDir).Load();

            state.IsComplete().ShouldBeTrue();
            state.ExitCode.ShouldBe(9);
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public void PersistedCompletion_RoundTripsAndKeepsSchemaKeys()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = StartScript(Abandoned(), "exit 4");
        WaitForPersistedComplete(ticket);

        var path = Path.Combine(WorkDir(ticket), "scriptstate.json");
        var state = new ScriptStateStore(WorkDir(ticket)).Load();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var knownKeys = new[] { "ticketId", "progress", "exitCode", "nextLogSequence", "createdAt", "startedAt", "completedAt", "processId", "processStartedAt", "processOwnerSignature" };

        state.IsComplete().ShouldBeTrue();
        state.ExitCode.ShouldBe(4);
        state.CompletedAt.ShouldNotBeNull();
        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldAllBe(n => knownKeys.Contains(n), "no new on-disk keys: an older agent reading this file must see only fields it already knows");
    }

    [Fact]
    public void PersistedStateSaysComplete_OnlyAfterFinalLogLineIsOnDisk()
    {
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i < 25; i++)
        {
            var ticket = StartScript(Abandoned(), $"echo noise; echo {FinalMarker}");
            var logAtComplete = ReadLogWhenStateFirstCompletes(ticket);

            logAtComplete.ShouldContain(FinalMarker, customMessage: $"iteration {i}: state said Complete before the last output line reached output.log");
        }
    }

    [Fact]
    public void InstantlyExitingScripts_AlwaysEndWithPersistedCompletion_NotOverwrittenByStartSnapshot()
    {
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i < 20; i++)
        {
            var ticket = StartScript(Abandoned(), "exit 2");

            WaitForPersistedComplete(ticket);

            new ScriptStateStore(WorkDir(ticket)).Load().ExitCode.ShouldBe(2, $"iteration {i}");
        }
    }

    [Fact]
    public void BackgroundChildOutlivingScript_CompleteStateWaitsForItsTailOutput()
    {
        if (OperatingSystem.IsWindows()) return;

        var ticket = StartScript(Abandoned(), $"echo head; (sleep 1; echo {FinalMarker}) & exit 0");

        var logAtComplete = ReadLogWhenStateFirstCompletes(ticket);

        logAtComplete.ShouldContain(FinalMarker, customMessage: "the shell exited but its child still held stdout; Complete must not be recorded before that tail is drained to disk");
    }

    [Fact]
    public void CompleteScript_AfterNaturalExitPersisted_RecordsSameExitCodeAgain()
    {
        if (OperatingSystem.IsWindows()) return;

        var factory = new RecordingStateStoreFactory();
        var service = new LocalScriptService(factory);
        var ticket = StartScript(service, "echo x; exit 6");
        WaitUntil(() => factory.CompleteExitCodes(ticket).Count > 0, "natural-exit persistence");

        var status = service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        status.ExitCode.ShouldBe(6);
        factory.CompleteExitCodes(ticket).ShouldAllBe(c => c == 6, "persisting twice must be idempotent");
    }

    [Fact]
    public void CancelRunningScript_NeverRecordsTheKillSignalExitCode()
    {
        if (OperatingSystem.IsWindows()) return;

        var factory = new RecordingStateStoreFactory();
        var service = new LocalScriptService(factory);
        var ticket = StartScript(service, "sleep 60");

        var status = service.CancelScript(new CancelScriptCommand(ticket, 0));
        Thread.Sleep(300);

        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        factory.CompleteExitCodes(ticket).ShouldNotBeEmpty();
        factory.CompleteExitCodes(ticket).ShouldAllBe(c => c == ScriptExitCodes.Canceled, "the exit callback fired by Kill must not record the kill signal's exit code as the result");
    }

    [Fact]
    public void CancelRacingNaturalExit_EndsWithSingleConsistentCanceledResult()
    {
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i < 30; i++)
        {
            var factory = new RecordingStateStoreFactory();
            var service = new LocalScriptService(factory);
            var ticket = StartScript(service, "exit 0");
            Thread.Sleep(i * 3);

            var status = service.CancelScript(new CancelScriptCommand(ticket, 0));
            Thread.Sleep(200);

            var codes = factory.CompleteExitCodes(ticket);
            status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
            codes.Last().ShouldBe(ScriptExitCodes.Canceled, $"iteration {i}: final recorded result must match the Canceled response; saw [{string.Join(",", codes)}]");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static StartScriptCommand NewCommand(string body) => new(new ScriptTicket(Guid.NewGuid().ToString("N")), body, ScriptIsolationLevel.NoIsolation, TimeSpan.FromMinutes(5), null, Array.Empty<string>(), null, TimeSpan.Zero);

    /// <summary>
    /// The "pre-crash" service. Kept referenced until Dispose: a real agent's service is a long-lived singleton, whereas a
    /// collected instance would take its runtime process-exit watcher with it and make these tests GC-timing dependent.
    /// The post-restart instance is a different object that never saw the script start.
    /// </summary>
    private LocalScriptService Abandoned()
    {
        var service = new LocalScriptService();
        _abandonedServices.Add(service);
        return service;
    }

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _tickets.Add(ticket.TaskId);
        return ticket;
    }

    private ScriptTicket StartScript(LocalScriptService service, string body)
    {
        var command = NewCommand(body);
        _tickets.Add(command.ScriptTicket.TaskId);
        service.StartScript(command);
        return command.ScriptTicket;
    }

    private static string WorkDir(ScriptTicket ticket) => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    private static void SaveState(ScriptTicket ticket, ScriptState state) => new ScriptStateStore(WorkDir(ticket)).Save(state);

    private static string AllText(ScriptStatusResponse status) => string.Join("\n", status.Logs.Select(l => l.Text));

    private static bool TryLoadState(ScriptTicket ticket, out ScriptState? state)
    {
        state = null;

        try
        {
            state = new ScriptStateStore(WorkDir(ticket)).Load();
            return true;
        }
        catch { return false; }
    }

    private static void WaitUntil(Func<bool> condition, string signal)
    {
        var sw = Stopwatch.StartNew();

        while (!condition())
        {
            if (sw.Elapsed > WaitLimit)
                throw new TimeoutException($"{signal} was not observed within {WaitLimit.TotalSeconds}s — inspect the scriptstate.json under the temp squid-tentacle-<ticket> dir");

            Thread.Sleep(2);
        }
    }

    private static void WaitForPersistedComplete(ScriptTicket ticket) => WaitUntil(() => TryLoadState(ticket, out var s) && s!.IsComplete(), "persisted Complete state");

    private static string ReadLogWhenStateFirstCompletes(ScriptTicket ticket)
    {
        WaitForPersistedComplete(ticket);

        var entries = new SequencedLogReader(Path.Combine(WorkDir(ticket), "output.log")).ReadFrom(-1);

        return string.Join("\n", entries.Select(e => e.Text));
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

    private sealed class RecordingStateStoreFactory : IScriptStateStoreFactory
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _completes = new();

        public IScriptStateStore Create(string workspace) => new RecordingStore(new ScriptStateStore(workspace), _completes);

        public List<int> CompleteExitCodes(ScriptTicket ticket) => _completes.TryGetValue(ticket.TaskId, out var q) ? q.ToList() : new List<int>();
    }

    private sealed class RecordingStore : IScriptStateStore
    {
        private readonly ScriptStateStore _inner;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _completes;

        public RecordingStore(ScriptStateStore inner, ConcurrentDictionary<string, ConcurrentQueue<int>> completes)
        {
            _inner = inner;
            _completes = completes;
        }

        public string WorkspacePath => _inner.WorkspacePath;

        public bool Exists() => _inner.Exists();

        public ScriptState Load() => _inner.Load();

        public void Delete() => _inner.Delete();

        public void Save(ScriptState state)
        {
            _inner.Save(state);

            if (state.IsComplete() && state.ExitCode.HasValue)
                _completes.GetOrAdd(state.TicketId, _ => new ConcurrentQueue<int>()).Enqueue(state.ExitCode.Value);
        }
    }
}
