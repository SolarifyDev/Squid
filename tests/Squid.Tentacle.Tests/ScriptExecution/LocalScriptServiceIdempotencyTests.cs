using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class LocalScriptServiceIdempotencyTests : IDisposable
{
    private readonly string _workspaceRoot = Path.Combine(Path.GetTempPath(), $"squid-idempotency-{Guid.NewGuid():N}");
    private readonly List<ScriptTicket> _createdTickets = new();
    private readonly List<LocalScriptService> _servicesToDispose = new();

    public LocalScriptServiceIdempotencyTests() => Directory.CreateDirectory(_workspaceRoot);

    public void Dispose()
    {
        foreach (var ticket in _createdTickets)
        {
            foreach (var service in _servicesToDispose)
            {
                try { service.CancelScript(new CancelScriptCommand(ticket, 0)); }
                catch { /* ignore */ }
            }
        }
        try { if (Directory.Exists(_workspaceRoot)) Directory.Delete(_workspaceRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void StartScript_SameTicketFiveTimes_OnlyOneProcessSpawned()
    {
        var service = CreateService();
        var counterPath = Path.Combine(_workspaceRoot, "launch-counter.txt");
        var command = MakeCounterCommand(counterPath);

        for (var i = 0; i < 5; i++)
            service.StartScript(command);

        // Allow the launched process a moment to write its counter line.
        Thread.Sleep(300);

        File.Exists(counterPath).ShouldBeTrue("launched process must have written to counter file");
        var lines = File.ReadAllLines(counterPath);
        lines.Length.ShouldBe(1, "same ScriptTicket invoked 5 times must spawn exactly 1 process (idempotent)");

        // The status should be Running after the first call; subsequent calls just mirror it.
        var status = service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));
        status.State.ShouldBe(ProcessState.Running);
    }

    [Fact]
    public void StartScript_AcrossTwoAgentInstances_SameTicket_OnlyOneProcessSpawned()
    {
        // Disk-backed idempotency: two separate LocalScriptService instances (simulating
        // an agent restart) receiving the same ScriptTicket must not double-launch.
        var counterPath = Path.Combine(_workspaceRoot, "multi-instance-counter.txt");
        var command = MakeCounterCommand(counterPath);

        var firstAgent = CreateService();
        firstAgent.StartScript(command);
        Thread.Sleep(300);

        var secondAgent = CreateService();
        secondAgent.StartScript(command);
        Thread.Sleep(300);

        File.Exists(counterPath).ShouldBeTrue();
        var lines = File.ReadAllLines(counterPath);
        lines.Length.ShouldBe(1, "redelivered StartScript after agent restart must not spawn a duplicate process");
    }

    [Fact]
    public void StartScript_Twice_FirstCompletes_SecondReturnsCompleteStatus()
    {
        var service = CreateService();
        var command = MakeEchoCommand("done", durationWait: TimeSpan.FromSeconds(3));

        var first = service.StartScript(command);
        // Give the process time to exit.
        Thread.Sleep(500);

        var second = service.StartScript(command);

        // Second call must not re-launch. It should return Complete (mirroring the first's outcome).
        second.State.ShouldBe(ProcessState.Complete);
        second.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void StartScript_StateFile_PersistedToDisk_AfterLaunch()
    {
        var service = CreateService();
        var command = MakeSleepCommand(durationSeconds: 30);

        service.StartScript(command);

        var workDir = FindWorkDir(command.ScriptTicket);
        var stateFile = Path.Combine(workDir, "scriptstate.json");

        File.Exists(stateFile).ShouldBeTrue("state file must be persisted on first StartScript so a restarted agent can recover");
    }

    [Fact]
    public void StartScript_DurationToWaitForScriptToFinish_FastScript_ReturnsCompleteInline()
    {
        var service = CreateService();
        var command = MakeEchoCommand("instant", durationWait: TimeSpan.FromSeconds(5));

        var status = service.StartScript(command);

        status.State.ShouldBe(ProcessState.Complete, "fast script must return Complete within DurationToWaitForScriptToFinish");
        status.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void StartScript_DurationZero_ReturnsRunning_ForLongScript()
    {
        var service = CreateService();
        var command = MakeSleepCommand(durationSeconds: 5);

        var status = service.StartScript(command);

        status.State.ShouldBe(ProcessState.Running, "with DurationToWaitForScriptToFinish=0 and a slow script, StartScript must return Running immediately");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private LocalScriptService CreateService()
    {
        var service = new LocalScriptService();
        _servicesToDispose.Add(service);
        return service;
    }

    private ScriptTicket NewTicket()
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        _createdTickets.Add(ticket);
        return ticket;
    }

    private StartScriptCommand MakeEchoCommand(string message, TimeSpan durationWait)
    {
        var ticket = NewTicket();
        return new StartScriptCommand(
            ticket,
            $"echo '{message}'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            durationWait);
    }

    private StartScriptCommand MakeSleepCommand(int durationSeconds)
    {
        var ticket = NewTicket();
        return MakeSleepCommandForTicket(ticket, durationSeconds);
    }

    private StartScriptCommand MakeCounterCommand(string counterPath)
    {
        var ticket = NewTicket();
        return new StartScriptCommand(
            ticket,
            $"echo launch >> '{counterPath}'; sleep 30",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }

    private static StartScriptCommand MakeSleepCommandForTicket(ScriptTicket ticket, int durationSeconds)
    {
        return new StartScriptCommand(
            ticket,
            $"sleep {durationSeconds}",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }

    private static string FindWorkDir(ScriptTicket ticket)
        => Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");

    private static int CountRunningBashForWorkDir(string workDir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return 0;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ps", "auxww")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var count = 0;
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("grep")) continue;
                if (line.Contains(workDir)) count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
}
