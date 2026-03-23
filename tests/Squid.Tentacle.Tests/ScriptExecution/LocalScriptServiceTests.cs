using System;
using System.IO;
using System.Threading;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class LocalScriptServiceTests : IDisposable
{
    private readonly LocalScriptService _service = new();
    private readonly List<string> _createdTickets = new();

    [Fact]
    public void StartScript_CreatesWorkDirectory()
    {
        var ticket = StartEchoScript("hello");

        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");
        Directory.Exists(workDir).ShouldBeTrue();
    }

    [Fact]
    public void StartScript_WritesScriptFile()
    {
        var ticket = StartEchoScript("hello world");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}", "script.sh");
        File.Exists(scriptPath).ShouldBeTrue();
        File.ReadAllText(scriptPath).ShouldContain("echo 'hello world'");
    }

    [Fact]
    public void StartScript_ReturnsUniqueTickets()
    {
        var ticket1 = StartEchoScript("1");
        var ticket2 = StartEchoScript("2");

        ticket1.TaskId.ShouldNotBe(ticket2.TaskId);
    }

    [Fact]
    public void GetStatus_RunningProcess_ReturnsRunning()
    {
        var ticket = _service.StartScript(MakeCommand("sleep 10"));
        _createdTickets.Add(ticket.TaskId);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Running);
    }

    [Fact]
    public void GetStatus_CompletedProcess_ReturnsCompleteWithExitCode()
    {
        var ticket = StartEchoScript("done");

        // Wait for process to complete
        Thread.Sleep(500);

        var status = _service.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void GetStatus_UnknownTicket_ReturnsUnknownResult()
    {
        var status = _service.GetStatus(new ScriptStatusRequest(new ScriptTicket("unknown"), 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void CompleteScript_CleansUpWorkDir()
    {
        var ticket = StartEchoScript("cleanup");

        Thread.Sleep(500);

        _service.CompleteScript(new CompleteScriptCommand(ticket, 0));

        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticket.TaskId}");
        Directory.Exists(workDir).ShouldBeFalse();
    }

    [Fact]
    public void CancelScript_KillsProcessAndReturnsCanceled()
    {
        var ticket = _service.StartScript(MakeCommand("sleep 60"));
        _createdTickets.Add(ticket.TaskId);

        var status = _service.CancelScript(new CancelScriptCommand(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    private ScriptTicket StartEchoScript(string message)
    {
        var ticket = _service.StartScript(MakeCommand($"echo '{message}'"));
        _createdTickets.Add(ticket.TaskId);

        return ticket;
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null);
    }

    public void Dispose()
    {
        foreach (var ticketId in _createdTickets)
        {
            try
            {
                _service.CancelScript(new CancelScriptCommand(new ScriptTicket(ticketId), 0));
            }
            catch { }

            try
            {
                var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{ticketId}");
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch { }
        }
    }
}
