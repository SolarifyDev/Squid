using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class RunningScriptReporterTests : IDisposable
{
    private readonly LocalScriptService _service = new();
    private readonly List<ScriptTicket> _createdTickets = new();

    public void Dispose()
    {
        foreach (var t in _createdTickets)
        {
            try { _service.CancelScript(new CancelScriptCommand(t, 0)); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void IsRunningScript_BeforeStartScript_ReturnsFalse()
    {
        var reporter = (IRunningScriptReporter)_service;

        reporter.IsRunningScript("never-started").ShouldBeFalse();
    }

    [Fact]
    public void IsRunningScript_NullOrEmptyTicketId_ReturnsFalse()
    {
        var reporter = (IRunningScriptReporter)_service;

        reporter.IsRunningScript(null!).ShouldBeFalse();
        reporter.IsRunningScript(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void IsRunningScript_DuringActiveScript_ReturnsTrue()
    {
        var reporter = (IRunningScriptReporter)_service;
        var command = MakeSleepCommand(durationSeconds: 30);

        _service.StartScript(command);
        _createdTickets.Add(command.ScriptTicket);

        reporter.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void IsRunningScript_AfterCompleteScript_ReturnsFalse()
    {
        var reporter = (IRunningScriptReporter)_service;
        var command = MakeEchoCommand("quick");

        _service.StartScript(command);
        Thread.Sleep(300);
        _service.CompleteScript(new CompleteScriptCommand(command.ScriptTicket, 0));

        reporter.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public void IsRunningScript_AfterCancelScript_ReturnsFalse()
    {
        var reporter = (IRunningScriptReporter)_service;
        var command = MakeSleepCommand(durationSeconds: 30);

        _service.StartScript(command);
        _service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));

        reporter.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse();
    }

    private static StartScriptCommand MakeSleepCommand(int durationSeconds)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            $"sleep {durationSeconds}",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }

    private static StartScriptCommand MakeEchoCommand(string message)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            $"echo '{message}'",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }
}
