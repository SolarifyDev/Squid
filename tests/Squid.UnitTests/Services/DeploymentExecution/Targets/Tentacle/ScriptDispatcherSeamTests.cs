using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Pins the seam itself: the strategy must route session creation, start and unknown-ticket recognition
/// through the dispatcher the <see cref="IScriptDispatcherSelector"/> picks (so a V2 sibling can replace V1
/// without touching the strategy), and <see cref="V1ScriptDispatcher"/> must own the V1 wire specifics.
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class ScriptDispatcherSeamTests
{
    private readonly Machine _machine = new() { Id = 42, Name = "agent-42", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-seam","Thumbprint":"AABBCCDD"}""" };

    [Fact]
    public async Task Strategy_UsesTheSelectedDispatcher_ForSessionStartAndSpec()
    {
        var session = new Mock<IScriptAgentSession>();
        ScriptDispatchSpec seenSpec = null;
        var started = 0;
        session.Setup(s => s.Version).Returns(ScriptProtocolVersion.V1);
        session.Setup(s => s.PrepareStart(It.IsAny<ScriptDispatchSpec>()))
            .Callback<ScriptDispatchSpec>(spec => seenSpec = spec)
            .Returns(() => { started++; return Task.FromResult(new AgentScriptStatus(seenSpec.Ticket, AgentScriptState.Running, 0, new List<ProcessOutput>(), 0)); });
        session.Setup(s => s.ObserveAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptTicket>(), It.IsAny<AgentScriptStatus>())).ReturnsAsync(new ScriptExecutionResult { Success = true });

        var dispatcher = new Mock<IScriptDispatcher>();
        dispatcher.Setup(d => d.Version).Returns(ScriptProtocolVersion.V1);
        dispatcher.Setup(d => d.Open(It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptSessionContext>())).Returns(session.Object);

        var selector = new Mock<IScriptDispatcherSelector>();
        selector.Setup(s => s.ForMachine(42)).Returns(dispatcher.Object);

        var factory = new Mock<IHalibutClientFactory>();
        var strategy = new HalibutMachineExecutionStrategy(factory.Object, new CalamariPayloadBuilder(new Mock<IYamlNuGetPacker>().Object), new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 }), new HalibutSetting(), dispatcherSelector: selector.Object);

        var request = new ScriptExecutionRequest
        {
            Machine = _machine, ScriptBody = "echo seam", ExecutionMode = ExecutionMode.DirectScript, Syntax = ScriptSyntax.Bash, TargetNamespace = "ns", ServerTaskId = 777, Timeout = TimeSpan.FromMinutes(3),
            ReleaseVersion = "1.0.0", Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };

        await strategy.ExecuteScriptAsync(request, CancellationToken.None);

        factory.Verify(f => f.CreateClient(It.IsAny<ServiceEndPoint>()), Times.Never, "the strategy must open the agent through the selected dispatcher, not the factory");
        dispatcher.Verify(d => d.Open(It.IsAny<ServiceEndPoint>(), It.Is<ScriptSessionContext>(c => c.ScriptTimeout == TimeSpan.FromMinutes(3))), Times.Once);
        started.ShouldBe(1);
        seenSpec.ScriptBody.ShouldBe("echo seam");
        seenSpec.ServerTaskId.ShouldBe(777);
        seenSpec.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
        seenSpec.IsolationMutexName.ShouldBe("squid/machine/42");
        seenSpec.Syntax.ShouldBe(ScriptSyntax.Bash);
        seenSpec.TargetNamespace.ShouldBe("ns");
        seenSpec.Ticket.TaskId.ShouldMatch("^[a-f0-9]{32}$");
    }

    [Theory]
    [InlineData(true, true)]    // session says unknown -> fresh dispatch, no re-attach
    [InlineData(false, false)]  // session says known   -> re-attach, no StartScript
    public async Task Strategy_AsksTheSession_WhetherTheRecordedTicketIsKnown(bool unknown, bool expectFreshStart)
    {
        var session = new Mock<IScriptAgentSession>();
        var started = 0;
        session.Setup(s => s.Version).Returns(ScriptProtocolVersion.V1);
        session.Setup(s => s.PrepareStart(It.IsAny<ScriptDispatchSpec>())).Returns(() => { started++; return Task.FromResult(new AgentScriptStatus(new ScriptTicket("t"), AgentScriptState.Running, 0, new List<ProcessOutput>(), 0)); });
        session.Setup(s => s.ProbeAsync(It.IsAny<ScriptTicket>())).ReturnsAsync(new AgentScriptStatus(new ScriptTicket("OLD"), AgentScriptState.Completed, 0, new List<ProcessOutput>(), 0));
        session.Setup(s => s.IsUnknownTicket(It.IsAny<AgentScriptStatus>())).Returns(unknown);
        session.Setup(s => s.ObserveAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptTicket>(), It.IsAny<AgentScriptStatus>())).ReturnsAsync(new ScriptExecutionResult { Success = true });

        var dispatcher = new Mock<IScriptDispatcher>();
        dispatcher.Setup(d => d.Version).Returns(ScriptProtocolVersion.V1);
        dispatcher.Setup(d => d.Open(It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptSessionContext>())).Returns(session.Object);

        var selector = new Mock<IScriptDispatcherSelector>();
        selector.Setup(s => s.ForMachine(It.IsAny<int>())).Returns(dispatcher.Object);

        var store = new Mock<IInFlightScriptStore>();
        store.Setup(s => s.TryGetTicketAsync(It.IsAny<int>(), It.IsAny<DispatchSlot>(), It.IsAny<CancellationToken>())).ReturnsAsync("OLD");

        var strategy = new HalibutMachineExecutionStrategy(new Mock<IHalibutClientFactory>().Object, new CalamariPayloadBuilder(new Mock<IYamlNuGetPacker>().Object), new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 }), new HalibutSetting(), inFlightStore: store.Object, dispatcherSelector: selector.Object);

        await strategy.ExecuteScriptAsync(new ScriptExecutionRequest { Machine = _machine, ScriptBody = "x", ExecutionMode = ExecutionMode.DirectScript, ReleaseVersion = "1", Variables = new List<Message.Models.Deployments.Variable.VariableDto>() }, CancellationToken.None);

        session.Verify(s => s.IsUnknownTicket(It.IsAny<AgentScriptStatus>()), Times.Once);
        (started == 1).ShouldBe(expectFreshStart);
    }

    [Fact]
    public void V1_PrepareStart_PerformsNoIo_UntilTheDelegateRuns()
    {
        var factory = new Mock<IHalibutClientFactory>();
        var agent = new Mock<IAsyncScriptService>();
        agent.Setup(a => a.StartScriptAsync(It.IsAny<StartScriptCommand>())).ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t"), ProcessState.Running, 0, new List<ProcessOutput>(), 0));

        var start = new V1ScriptDispatcher(factory.Object).PrepareStart(agent.Object, Spec());

        agent.Verify(a => a.StartScriptAsync(It.IsAny<StartScriptCommand>()), Times.Never);

        start().Wait();

        agent.Verify(a => a.StartScriptAsync(It.Is<StartScriptCommand>(c => c.TaskId == "9" && c.IsolationMutexName == "mutex" && c.ScriptBody == "body")), Times.Once);
    }

    [Fact]
    public void V1_PrepareStart_UnsupportedSyntax_ThrowsBeforeAnyRpc()
    {
        var agent = new Mock<IAsyncScriptService>(MockBehavior.Strict);

        Should.Throw<InvalidOperationException>(() => new V1ScriptDispatcher(new Mock<IHalibutClientFactory>().Object).PrepareStart(agent.Object, Spec() with { Syntax = (ScriptSyntax)999 }));
    }

    [Fact]
    public void V1_CreateClient_DelegatesToTheFactory()
    {
        var agent = new Mock<IAsyncScriptService>().Object;
        var factory = new Mock<IHalibutClientFactory>();
        var endpoint = new ServiceEndPoint("poll://x/", "AABB", new HalibutTimeoutsAndLimits());
        factory.Setup(f => f.CreateClient(endpoint)).Returns(agent);

        new V1ScriptDispatcher(factory.Object).CreateClient(endpoint).ShouldBeSameAs(agent);
    }

    [Theory]
    [InlineData(ProcessState.Complete, ScriptExitCodes.UnknownResult, true)]
    [InlineData(ProcessState.Complete, 0, false)]
    [InlineData(ProcessState.Running, 0, false)]
    [InlineData(ProcessState.Pending, 0, false)]
    public void V1_IsUnknownTicket_OnlyForCompleteWithUnknownResult(ProcessState state, int exitCode, bool expected)
    {
        var probe = new ScriptStatusResponse(new ScriptTicket("t"), state, exitCode, new List<ProcessOutput>(), 0);

        new V1ScriptDispatcher(new Mock<IHalibutClientFactory>().Object).IsUnknownTicket(probe).ShouldBe(expected);
    }

    [Fact]
    public void V1_IsUnknownTicket_NullProbe_IsUnknown()
        => new V1ScriptDispatcher(new Mock<IHalibutClientFactory>().Object).IsUnknownTicket(null).ShouldBeTrue();

    private static ScriptDispatchSpec Spec() => new()
    {
        Ticket = new ScriptTicket("ticket"),
        ScriptBody = "body",
        Files = Array.Empty<ScriptFile>(),
        Timeout = TimeSpan.FromMinutes(2),
        IsolationMutexName = "mutex",
        ServerTaskId = 9,
        Syntax = ScriptSyntax.Bash,
        TargetNamespace = "ns"
    };
}
