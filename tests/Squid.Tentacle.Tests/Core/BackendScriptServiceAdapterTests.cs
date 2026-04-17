using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class BackendScriptServiceAdapterTests
{
    [Fact]
    public void Adapter_Delegates_All_Script_Service_Calls_To_Backend()
    {
        var backend = new FakeScriptBackend();
        var adapter = new BackendScriptServiceAdapter(backend);

        var ticket = new ScriptTicket("ticket-123");
        var command = new StartScriptCommand(
            ticket,
            "echo hello",
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromSeconds(10),
            isolationMutexName: null,
            arguments: Array.Empty<string>(),
            taskId: null,
            durationToWaitForScriptToFinish: TimeSpan.Zero);
        var statusRequest = new ScriptStatusRequest(ticket, 5);
        var completeCommand = new CompleteScriptCommand(ticket, 9);
        var cancelCommand = new CancelScriptCommand(ticket, 11);

        var startResult = adapter.StartScript(command);
        var statusResult = adapter.GetStatus(statusRequest);
        var completeResult = adapter.CompleteScript(completeCommand);
        var cancelResult = adapter.CancelScript(cancelCommand);

        startResult.ShouldBe(backend.StartResult);
        statusResult.ShouldBe(backend.StatusResult);
        completeResult.ShouldBe(backend.CompleteResult);
        cancelResult.ShouldBe(backend.CancelResult);

        backend.StartCalls.ShouldBe(1);
        backend.GetStatusCalls.ShouldBe(1);
        backend.CompleteCalls.ShouldBe(1);
        backend.CancelCalls.ShouldBe(1);

        backend.LastStartCommand.ShouldBe(command);
        backend.LastStatusRequest.ShouldBe(statusRequest);
        backend.LastCompleteCommand.ShouldBe(completeCommand);
        backend.LastCancelCommand.ShouldBe(cancelCommand);
    }
}
