using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Exceptions;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Machines;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Fidelity: HIGH for the canceller and the dispatch seam (real <see cref="ScriptDispatcherSelector"/>, real V1 / V2 dispatchers and sessions, the real
/// retry decorators); only the agents (wire) and the two data providers are faked. A pause leaves the agent script running; cancelling the paused
/// task afterwards must send each recorded ticket a CancelScript in the dialect it was started with, and an agent that is down must never hold the
/// cancel hostage.
/// </summary>
public class HalibutInFlightScriptCancellerTests
{
    private const int TaskId = 7;

    private readonly List<string> _events = new();
    private readonly Mock<IInFlightScriptStore> _store = new();
    private readonly Mock<IMachineDataProvider> _machines = new();

    [Fact]
    public async Task EachRecordedTicket_GetsACancel_InTheDialectItWasStartedWith()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v1-ticket", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(2, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1, 2);

        await Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId);

        _events.ShouldBe(["V1.Cancel:v1-ticket", "V2.Cancel:v2-ticket"], ignoreOrder: true);
    }

    // The recorded version wins over what the agent advertises now: a V2-recorded script is only addressable through V2.
    [Fact]
    public async Task ARecordWithoutAVersion_IsALegacyV1Script_AndNeverOpensAV2Client()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "legacy", ScriptProtocolVersion.V1));
        Machines(1);
        var factories = new FakeClientFactories(new FakeV1Agent(_events), new FakeV2Agent(_events));

        await Canceller(factories).CancelAllAsync(TaskId);

        _events.ShouldBe(["V1.Cancel:legacy"]);
        factories.V2Opened.ShouldBe(0);
    }

    [Fact]
    public async Task TwoScriptsOnOneMachine_AreBothCancelled()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "a", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(1, 11, 101), "b", ScriptProtocolVersion.V1));
        Machines(1);

        await Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId);

        _events.ShouldBe(["V1.Cancel:a", "V1.Cancel:b"], ignoreOrder: true);
    }

    // The machine provider shares the caller's one scoped DbContext, which allows a single operation at a time.
    [Fact]
    public async Task MachineLookups_NeverOverlap_AndEveryScriptIsStillCancelled()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "a", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(2, 10, 100), "b", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(3, 10, 100), "c", ScriptProtocolVersion.V1));
        var inFlight = 0;
        var overlapped = false;

        foreach (var id in new[] { 1, 2, 3 })
            _machines.Setup(m => m.GetMachinesByIdAsync(id, It.IsAny<CancellationToken>())).Returns(async () =>
            {
                if (Interlocked.Increment(ref inFlight) > 1) overlapped = true;

                await Task.Delay(50);
                Interlocked.Decrement(ref inFlight);

                return new Machine { Id = id, Name = $"agent-{id}", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-cancel","Thumbprint":"AABBCCDD"}""" };
            });

        await Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId);

        overlapped.ShouldBeFalse("two lookups on one DbContext at once make EF throw, which would silently skip the cancel");
        _events.ShouldBe(["V1.Cancel:a", "V1.Cancel:b", "V1.Cancel:c"], ignoreOrder: true);
    }

    [Fact]
    public async Task AMachineLookupThatThrows_SkipsOnlyThatMachinesScripts()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "broken", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(2, 10, 100), "fine", ScriptProtocolVersion.V1));
        _machines.Setup(m => m.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db blip"));
        Machines(2);

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId));

        _events.ShouldBe(["V1.Cancel:fine"]);
    }

    [Fact]
    public async Task NothingInFlight_TouchesNoAgent()
    {
        Recorded();
        var factories = new FakeClientFactories(new FakeV1Agent(_events), new FakeV2Agent(_events));

        await Canceller(factories).CancelAllAsync(TaskId);

        factories.V1Opened.ShouldBe(0);
        factories.V2Opened.ShouldBe(0);
    }

    // One unreachable agent must neither throw nor stop the reachable one from being cancelled.
    [Fact]
    public async Task AnUnreachableAgent_IsLogged_NotThrown_AndTheOthersAreStillCancelled()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "down", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(2, 10, 100), "up", ScriptProtocolVersion.V2));
        Machines(1, 2);
        var v2 = new FakeV2Agent(_events);

        await Should.NotThrowAsync(() => Canceller(new ThrowingV1Agent(), v2).CancelAllAsync(TaskId));

        _events.ShouldBe(["V2.Cancel:up"]);
    }

    // An agent that accepts the call and never answers must not hold the cancel past the retry budget plus the grace.
    [Fact]
    public async Task AHangingAgent_IsAbandonedWithinTheBound()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "hung", ScriptProtocolVersion.V1));
        Machines(1);
        var canceller = Canceller(new HangingV1Agent(), new FakeV2Agent(_events), cancelBudget: TimeSpan.FromMilliseconds(100), grace: TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();

        await canceller.CancelAllAsync(TaskId);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the cancel must be bounded by CancelBudget + grace, not by the agent");
    }

    [Fact]
    public async Task AMachineThatNoLongerExists_IsSkipped_AndTheOthersAreCancelled()
    {
        Recorded(new InFlightScript(new DispatchSlot(9, 10, 100), "gone", ScriptProtocolVersion.V1), new InFlightScript(new DispatchSlot(1, 10, 100), "here", ScriptProtocolVersion.V1));
        Machines(1);

        await Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId);

        _events.ShouldBe(["V1.Cancel:here"]);
    }

    // The script lives in a dialect this server cannot speak: neither guess V1 nor throw.
    [Fact]
    public async Task AnUnrecognisedProtocol_IsNotCancelled_AndDoesNotThrow()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "future", null));
        Machines(1);

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId));

        _events.ShouldBeEmpty();
    }

    // ── Agent downgraded since the V2 ticket was recorded ───────────────────

    private static MachineRuntimeCapabilities V1OnlyAgent() => new() { SupportedServices = new[] { "IScriptService/v1" } };

    private static MachineRuntimeCapabilities V2Agent() => new() { SupportedServices = new[] { "IScriptService/v1", "IScriptService/v2" } };

    [Theory]
    [InlineData(typeof(ServiceNotFoundHalibutClientException))]
    [InlineData(typeof(MethodNotFoundHalibutClientException))]
    [InlineData(typeof(HalibutClientException))]
    public async Task AV2Cancel_ThatFails_WhenTheAgentWithdrewV2_IsRetriedOverV1(Type failure)
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V1OnlyAgent() };
        var v2 = new FakeV2Agent(_events) { CancelFailure = (Exception)Activator.CreateInstance(failure, "no such service") };

        await Canceller(new FakeV1Agent(_events), v2, refresher: refresher).CancelAllAsync(TaskId);

        _events.ShouldBe(["V2.Cancel:v2-ticket", "Caps.Refresh:1", "V1.Cancel:v2-ticket"]);
    }

    [Fact]
    public async Task AV2Cancel_ThatFails_WhileTheAgentStillAdvertisesV2_DoesNotFallBack()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V2Agent() };
        var v2 = new FakeV2Agent(_events) { CancelFailure = new HalibutClientException("connection reset") };

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), v2, refresher: refresher).CancelAllAsync(TaskId));

        _events.ShouldNotContain("V1.Cancel:v2-ticket");
        refresher.RefreshCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AV2Cancel_ThatFails_WhenTheAgentDoesNotAnswerCapabilities_DoesNotFallBack()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = null };
        var v2 = new FakeV2Agent(_events) { CancelFailure = new HalibutClientException("unreachable") };

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), v2, refresher: refresher).CancelAllAsync(TaskId));

        _events.ShouldNotContain("V1.Cancel:v2-ticket");
    }

    [Fact]
    public async Task AV2Cancel_ThatFailsWithANonProtocolError_DoesNotAskTheAgentOrFallBack()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V1OnlyAgent() };
        var v2 = new FakeV2Agent(_events) { CancelFailure = new InvalidOperationException("boom") };

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), v2, refresher: refresher).CancelAllAsync(TaskId));

        refresher.RefreshCalls.ShouldBe(0);
        _events.ShouldNotContain("V1.Cancel:v2-ticket");
    }

    [Fact]
    public async Task AV1Cancel_ThatFails_NeverAsksTheAgentForCapabilities()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v1-ticket", ScriptProtocolVersion.V1));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V1OnlyAgent() };

        await Should.NotThrowAsync(() => Canceller(new ThrowingV1Agent(), new FakeV2Agent(_events), refresher: refresher).CancelAllAsync(TaskId));

        refresher.RefreshCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AFailingV2Cancel_WithoutARefresher_DoesNotFallBack()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var v2 = new FakeV2Agent(_events) { CancelFailure = new ServiceNotFoundHalibutClientException("no such service") };

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), v2).CancelAllAsync(TaskId));

        _events.ShouldBe(["V2.Cancel:v2-ticket"]);
    }

    // The whole cancel of one script (V2 attempt + capabilities refresh + V1 retry) shares ONE deadline of CancelBudget + grace: cancelling a paused task is an HTTP request.
    [Fact]
    public async Task AnUnreachableV2Agent_WhoseCapabilitiesCallHangs_IsAbandonedWithinTheOneBound()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V1OnlyAgent(), RefreshDelay = Timeout.InfiniteTimeSpan };
        var v2 = new FakeV2Agent(_events) { CancelFailure = new HalibutClientException("unreachable") };
        var canceller = Canceller(new FakeV1Agent(_events), v2, cancelBudget: TimeSpan.FromMilliseconds(100), grace: TimeSpan.FromMilliseconds(200), refresher: refresher);

        await canceller.CancelAllAsync(TaskId).WaitAsync(TimeSpan.FromSeconds(10));

        _events.ShouldNotContain("V1.Cancel:v2-ticket");
    }

    [Fact]
    public async Task ADowngradeCancel_WhoseRefreshAndV1RetryAreSlow_StillEndsAtTheOriginalBound_NotASecondBudgetLater()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var budget = TimeSpan.FromSeconds(1.5);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V1OnlyAgent(), RefreshDelay = TimeSpan.FromSeconds(2.7) };
        var v2 = new FakeV2Agent(_events) { CancelFailure = new ServiceNotFoundHalibutClientException("no such service") };
        var canceller = Canceller(new HangingV1Agent(), v2, cancelBudget: budget, grace: budget, refresher: refresher);
        var watch = Stopwatch.StartNew();

        await canceller.CancelAllAsync(TaskId).WaitAsync(TimeSpan.FromSeconds(15));

        _events.ShouldContain("Caps.Refresh:1");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3.6), "refresh (2.7s) + a second full 1.5s cancel budget would end at 4.2s; the one shared deadline ends it at 3.0s");
    }

    [Fact]
    public async Task ADowngradeCancel_WithinTheBound_StillCancelsOverV1()
    {
        Recorded(new InFlightScript(new DispatchSlot(1, 10, 100), "v2-ticket", ScriptProtocolVersion.V2));
        Machines(1);
        var refresher = new FakeCapabilitiesRefresher(_events) { AgentAnswer = V1OnlyAgent(), RefreshDelay = TimeSpan.FromMilliseconds(50) };
        var v2 = new FakeV2Agent(_events) { CancelFailure = new ServiceNotFoundHalibutClientException("no such service") };

        await Canceller(new FakeV1Agent(_events), v2, refresher: refresher).CancelAllAsync(TaskId).WaitAsync(TimeSpan.FromSeconds(10));

        _events.ShouldBe(["V2.Cancel:v2-ticket", "Caps.Refresh:1", "V1.Cancel:v2-ticket"]);
    }

    [Fact]
    public async Task AFailingStoreRead_DoesNotThrow()
    {
        _store.Setup(s => s.ListAsync(TaskId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => Canceller(new FakeV1Agent(_events), new FakeV2Agent(_events)).CancelAllAsync(TaskId));
    }

    private void Recorded(params InFlightScript[] scripts)
        => _store.Setup(s => s.ListAsync(TaskId, It.IsAny<CancellationToken>())).ReturnsAsync(scripts);

    private void Machines(params int[] ids)
    {
        foreach (var id in ids)
            _machines.Setup(m => m.GetMachinesByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(new Machine { Id = id, Name = $"agent-{id}", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-cancel","Thumbprint":"AABBCCDD"}""" });
    }

    private HalibutInFlightScriptCanceller Canceller(IAsyncScriptService v1, IAsyncScriptServiceV2 v2, TimeSpan? cancelBudget = null, TimeSpan? grace = null, IMachineCapabilitiesRefresher refresher = null)
        => Canceller(new FakeClientFactories(v1, v2), cancelBudget, grace, refresher);

    private HalibutInFlightScriptCanceller Canceller(FakeClientFactories factories, TimeSpan? cancelBudget = null, TimeSpan? grace = null, IMachineCapabilitiesRefresher refresher = null)
    {
        var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(factories), new V2ScriptDispatcher(factories) });
        var budget = cancelBudget ?? TimeSpan.FromMilliseconds(200);

        return new HalibutInFlightScriptCanceller(_store.Object, _machines.Object, selector, new HalibutScriptObserver(), new RpcRetryHandler(budget, budget, budget), grace ?? TimeSpan.FromSeconds(1), refresher);
    }

    private sealed class ThrowingV1Agent : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => throw new HalibutClientException("agent unreachable");

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => throw new NotSupportedException();
    }

    private sealed class HangingV1Agent : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => new TaskCompletionSource<ScriptStatusResponse>().Task;

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => throw new NotSupportedException();
    }
}
