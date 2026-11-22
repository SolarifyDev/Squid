using System;
using System.Collections.Generic;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Wiring/seam pin: <see cref="ScriptExecutionRequest.ShouldCancelRemoteScript"/> must reach the observer
/// on BOTH dispatch paths of <see cref="HalibutMachineExecutionStrategy"/> — a fresh dispatch and a
/// re-attach on resume. A decision tested only inside the observer proves nothing if a call site stops
/// passing it (the observer then silently falls back to cancel-on-every-interruption).
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class HalibutMachineExecutionStrategyCancelDecisionTests
{
    private readonly Mock<IHalibutClientFactory> _factory = new();
    private readonly Mock<IAsyncScriptService> _agent = new();
    private readonly Mock<IHalibutScriptObserver> _observer = new();
    private readonly Mock<IInFlightScriptStore> _store = new();
    private readonly Mock<ICalamariPayloadBuilder> _payload = new();

    public HalibutMachineExecutionStrategyCancelDecisionTests()
    {
        _factory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>())).Returns(_agent.Object);
        _agent.Setup(a => a.StartScriptAsync(It.IsAny<StartScriptCommand>())).ReturnsAsync(Status("fresh", ProcessState.Running));
        _observer.Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>(), It.IsAny<RemoteScriptCancelDecision>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });
    }

    [Fact]
    public async Task FreshDispatch_PassesRequestDecisionToObserver()
    {
        RemoteScriptCancelDecision decision = () => false;

        await NewStrategy().ExecuteScriptAsync(Request(decision, machineId: 9101), CancellationToken.None);

        VerifyObserverReceived(decision);
    }

    [Fact]
    public async Task Reattach_PassesRequestDecisionToObserver()
    {
        // A recorded ticket the agent still holds → TryReattachAsync observes it (the resume path).
        _store.Setup(s => s.TryGetTicketAsync(It.IsAny<int>(), It.IsAny<DispatchSlot>(), It.IsAny<CancellationToken>())).ReturnsAsync("recorded");
        _agent.Setup(a => a.GetStatusAsync(It.IsAny<ScriptStatusRequest>())).ReturnsAsync(Status("recorded", ProcessState.Running));
        RemoteScriptCancelDecision decision = () => false;

        await NewStrategy().ExecuteScriptAsync(Request(decision, machineId: 9102), CancellationToken.None);

        _agent.Verify(a => a.StartScriptAsync(It.IsAny<StartScriptCommand>()), Times.Never, "precondition: this must be the re-attach path, not a fresh dispatch");
        VerifyObserverReceived(decision);
    }

    [Fact]
    public async Task NoDecisionOnRequest_PassesNull_PreservingLegacyAlwaysCancel()
    {
        await NewStrategy().ExecuteScriptAsync(Request(null, machineId: 9103), CancellationToken.None);

        _observer.Verify(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>(), null), Times.Once);
    }

    private void VerifyObserverReceived(RemoteScriptCancelDecision decision)
        => _observer.Verify(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>(), It.Is<RemoteScriptCancelDecision>(d => ReferenceEquals(d, decision))), Times.Once,
            "the strategy must hand request.ShouldCancelRemoteScript to the observer on this path");

    private HalibutMachineExecutionStrategy NewStrategy()
        => new(_factory.Object, _payload.Object, _observer.Object, new HalibutSetting(), inFlightStore: _store.Object);

    private static ScriptExecutionRequest Request(RemoteScriptCancelDecision decision, int machineId) => new()
    {
        ExecutionMode = ExecutionMode.DirectScript,
        ScriptBody = "echo hi",
        ServerTaskId = 1,
        StepId = 1,
        ActionId = 1,
        ShouldCancelRemoteScript = decision,
        Variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>(),
        Machine = new Machine
        {
            Id = machineId,
            Name = $"agent-{machineId}",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-x","Thumbprint":"AABBCCDD"}"""
        }
    };

    private static ScriptStatusResponse Status(string ticket, ProcessState state)
        => new(new ScriptTicket(ticket), state, 0, new List<ProcessOutput>(), 0);
}
