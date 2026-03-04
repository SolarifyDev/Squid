using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Tests.Support.Fakes;

public sealed class FakeScriptBackend : ITentacleScriptBackend
{
    public StartScriptCommand LastStartCommand { get; private set; }
    public ScriptStatusRequest LastStatusRequest { get; private set; }
    public CompleteScriptCommand LastCompleteCommand { get; private set; }
    public CancelScriptCommand LastCancelCommand { get; private set; }

    public int StartCalls { get; private set; }
    public int GetStatusCalls { get; private set; }
    public int CompleteCalls { get; private set; }
    public int CancelCalls { get; private set; }

    public ScriptTicket StartResult { get; set; } = new("fake-ticket");
    public ScriptStatusResponse StatusResult { get; set; } = new(
        new ScriptTicket("fake-ticket"),
        ProcessState.Running,
        0,
        new List<ProcessOutput>(),
        0);
    public ScriptStatusResponse CompleteResult { get; set; } = new(
        new ScriptTicket("fake-ticket"),
        ProcessState.Complete,
        0,
        new List<ProcessOutput>(),
        0);
    public ScriptStatusResponse CancelResult { get; set; } = new(
        new ScriptTicket("fake-ticket"),
        ProcessState.Complete,
        -1,
        new List<ProcessOutput>(),
        0);

    public ScriptTicket StartScript(StartScriptCommand command)
    {
        StartCalls++;
        LastStartCommand = command;
        return StartResult;
    }

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request)
    {
        GetStatusCalls++;
        LastStatusRequest = request;
        return StatusResult;
    }

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command)
    {
        CompleteCalls++;
        LastCompleteCommand = command;
        return CompleteResult;
    }

    public ScriptStatusResponse CancelScript(CancelScriptCommand command)
    {
        CancelCalls++;
        LastCancelCommand = command;
        return CancelResult;
    }
}
