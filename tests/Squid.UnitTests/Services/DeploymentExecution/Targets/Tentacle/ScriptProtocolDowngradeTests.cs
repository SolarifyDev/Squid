using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Exceptions;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// An agent that was downgraded (UpgradeMachineCommand supports downgrades) stops serving ScriptService V2, but
/// answers a V2 call with a plain transport exception the classifier treats as a blip — so before the fix a V2 ticket
/// paused on every resume forever, each pause charging the machine breaker. These pin the decisions the strategy now
/// takes, each against a refresher whose agent answer is scripted: V2 withdrawn (answered, no V2) vs still advertised
/// vs unanswered, for a fresh dispatch and for a resume.
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class ScriptProtocolDowngradeTests
{
    private const int MachineId = 42;
    private static readonly DispatchSlot Slot = new(MachineId, 11, 22);

    private readonly List<string> _events = new();
    private readonly FakeV1Agent _v1;
    private readonly FakeV2Agent _v2;
    private readonly FakeClientFactories _factories;
    private readonly VersionedFakeStore _store;
    private readonly FakeCapabilitiesRefresher _refresher;
    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();
    private readonly MachineCircuitBreakerRegistry _breakers = new(new HalibutSetting());
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Machine _machine = new() { Id = MachineId, Name = "agent-42", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-dg","Thumbprint":"AABBCCDD"}""" };

    public ScriptProtocolDowngradeTests()
    {
        _v1 = new FakeV1Agent(_events);
        _v2 = new FakeV2Agent(_events);
        _factories = new FakeClientFactories(_v1, _v2);
        _store = new VersionedFakeStore(_events);
        _refresher = new FakeCapabilitiesRefresher(_events);

        // This pod's cache is stale: it still advertises V2 although the agent was downgraded.
        _cache.Store(MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 });
    }

    private static MachineRuntimeCapabilities V1OnlyAgent() => new() { SupportedServices = new[] { "IScriptService/v1" } };

    private static MachineRuntimeCapabilities V2Agent() => new() { SupportedServices = new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 } };

    // ── Fresh dispatch ───────────────────────────────────────────────────────

    [Fact]
    public async Task FreshV2Start_Fails_AgentAnswersItNoLongerAdvertisesV2_DispatchesTheSameTicketOverV1_AndDoesNotChargeTheBreaker()
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        FailEveryV2Start(new HalibutClientException("read past the end of the stream"));

        var result = await Strategy().ExecuteScriptAsync(Request(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        var ticket = _v1.StartCommands.ShouldHaveSingleItem().ScriptTicket.TaskId;
        _events.Where(e => e.StartsWith("Store.Record")).ShouldBe(new[] { $"Store.Record:42/11/22:{ticket}:V2", $"Store.Record:42/11/22:{ticket}:V1" }, "the pointer is re-recorded as V1 BEFORE the V1 start, so a crash in between resumes in the right dialect");
        _events.IndexOf($"Store.Record:42/11/22:{ticket}:V1").ShouldBeLessThan(_events.IndexOf($"V1.Start:{ticket}"));
        _v2.StartCommands.ShouldBeEmpty("a V1-only agent never ran the V2 start");
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(0, "a protocol mismatch is not an agent outage");
        _events.ShouldContain("Store.Clear:42/11/22");
    }

    [Theory]
    [InlineData(typeof(ServiceNotFoundHalibutClientException))]
    [InlineData(typeof(MethodNotFoundHalibutClientException))]
    public async Task FreshV2Start_AgentLacksTheServiceOutright_AndWithdrewV2_FallsBackToV1(Type failure)
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        FailEveryV2Start((Exception)Activator.CreateInstance(failure, "no such service"));

        var result = await Strategy().ExecuteScriptAsync(Request(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _v1.StartCalls.ShouldBe(1);
    }

    [Fact]
    public async Task FreshV2Start_Fails_ButTheAgentStillAdvertisesV2_RethrowsAndChargesTheBreaker_NeverFallsBack()
    {
        _refresher.AgentAnswer = V2Agent();
        FailEveryV2Start(new HalibutClientException("connection reset"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _v1.StartCalls.ShouldBe(0, "V2 is still advertised, so this is a genuine transient failure: the deployment pauses and resumes over V2");
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task FreshV2Start_Fails_ButTheAgentDoesNotAnswerTheCapabilitiesCall_RethrowsAndChargesTheBreaker()
    {
        _refresher.AgentAnswer = null;
        FailEveryV2Start(new HalibutClientException("unreachable"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _refresher.RefreshCalls.ShouldBe(1);
        _v1.StartCalls.ShouldBe(0, "an unanswered capabilities call proves nothing about the agent's protocol");
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task FreshV2Start_FailsWithANonTransientError_IsNotAProtocolMismatch_NoRefresh()
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        FailEveryV2Start(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _refresher.RefreshCalls.ShouldBe(0);
        _v1.StartCalls.ShouldBe(0);
    }

    [Fact]
    public async Task V1Start_Fails_NeverRefreshesCapabilities()
    {
        _cache.Invalidate(MachineId);
        _refresher.AgentAnswer = V1OnlyAgent();
        _v1.StartFailures.Enqueue(new HalibutClientException("response lost"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _refresher.RefreshCalls.ShouldBe(0, "only a failed V2 call can be a protocol mismatch");
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task EveryDispatch_FirstAdoptsTheSharedCapabilitiesSnapshot_ForThisMachine()
    {
        _cache.Invalidate(MachineId);

        await Strategy().ExecuteScriptAsync(Request(), CancellationToken.None);

        _refresher.SyncedMachines.ShouldBe(new[] { MachineId });
        _events.First().ShouldBe("Caps.Sync:42", "the snapshot is adopted before the protocol version is chosen");
    }

    // ── Resume ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_V2TicketProbeFails_AgentWithdrewV2_V1KnowsTheTicket_ReattachesOverV1_NeverReruns()
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("read past the end of the stream");
        _v1.Know("OLD");

        var result = await Strategy().ExecuteScriptAsync(Request(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _v1.StartCalls.ShouldBe(0, "the unreleased ticket is still live on the agent: observe it, never start a second one");
        _v2.StartCalls.ShouldBe(0);
        _events.ShouldContain(e => e.StartsWith("V1.GetStatus:OLD"));
        _events.ShouldContain(e => e.StartsWith("V1.Complete:OLD"));
        _events.ShouldContain("Store.Clear:42/11/22");
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task Resume_V2TicketProbeFails_AgentWithdrewV2_V1DoesNotKnowTheTicket_FailsTheActionAsLost_NeverReruns()
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("read past the end of the stream");

        var result = await Strategy().ExecuteScriptAsync(Request(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
        result.StderrLines.ShouldHaveSingleItem().ShouldContain("did NOT run it again");
        _v1.StartCalls.ShouldBe(0, "V1 'unknown' for a V2-recorded ticket means released or lost — it may have run, so it is never re-dispatched");
        _v2.StartCalls.ShouldBe(0);
        _events.ShouldContain("Store.Clear:42/11/22", "the outcome is definitive (failed), so the pointer is released and the next resume does not pause again");
        _events.ShouldNotContain(e => e.StartsWith("Store.Record"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task Resume_V2TicketProbeFails_ButTheAgentStillAdvertisesV2_PausesAgain_PointerPreserved_NoV1Probe()
    {
        _refresher.AgentAnswer = V2Agent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("connection reset");

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _events.ShouldNotContain(e => e.StartsWith("V1."), "a V1 'unknown' would be mapped onto a ticket V2 still owns");
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear") || e.StartsWith("Store.Record"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task Resume_V2TicketProbeFails_AgentDoesNotAnswerCapabilities_PausesAgain_PointerPreserved()
    {
        _refresher.AgentAnswer = null;
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("unreachable");

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _events.ShouldNotContain(e => e.StartsWith("V1."));
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"));
    }

    [Fact]
    public async Task Resume_AfterWithdrawal_TheV1ProbeItselfFailingTransiently_PausesAndPreservesThePointer_NeverFresh()
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("read past the end of the stream");
        var flaky = new ThrowingV1Agent(new HalibutClientException("down again"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy(new FakeClientFactories(flaky, _v2)).ExecuteScriptAsync(Request(), CancellationToken.None));

        flaky.StartCalls.ShouldBe(0);
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear") || e.StartsWith("Store.Record"));
    }

    [Fact]
    public async Task Resume_AfterWithdrawal_TheV1ProbeFailingNonTransiently_IsNotReadAsUnknown_NeverFresh()
    {
        // V1 normally treats a non-transient probe failure as "dispatch fresh"; for a ticket V2 recorded that would re-run it.
        _refresher.AgentAnswer = V1OnlyAgent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("read past the end of the stream");
        var broken = new ThrowingV1Agent(new InvalidOperationException("v1 broke"));

        await Should.ThrowAsync<InvalidOperationException>(() => Strategy(new FakeClientFactories(broken, _v2)).ExecuteScriptAsync(Request(), CancellationToken.None));

        broken.StartCalls.ShouldBe(0);
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"));
    }

    [Fact]
    public async Task Resume_V2ProbeFailsNonTransiently_IsNotAProtocolMismatch_NoRefresh_PointerPreserved()
    {
        _refresher.AgentAnswer = V1OnlyAgent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new InvalidOperationException("boom");

        await Should.ThrowAsync<InvalidOperationException>(() => Strategy().ExecuteScriptAsync(Request(), CancellationToken.None));

        _refresher.RefreshCalls.ShouldBe(0);
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"));
    }

    [Fact]
    public async Task Resume_RecordedV1_ProbeFails_NeverRefreshesCapabilities_BehavesAsBefore()
    {
        _cache.Invalidate(MachineId);
        _refresher.AgentAnswer = V1OnlyAgent();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V1);
        var broken = new ThrowingV1Agent(new HalibutClientException("down"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy(new FakeClientFactories(broken, _v2)).ExecuteScriptAsync(Request(), CancellationToken.None));

        _refresher.RefreshCalls.ShouldBe(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void FailEveryV2Start(Exception failure)
    {
        for (var i = 0; i < 100; i++) _v2.StartFailuresBeforeRunning.Enqueue(failure);
    }

    private HalibutMachineExecutionStrategy Strategy(FakeClientFactories factories = null)
    {
        factories ??= _factories;

        var retry = new RpcRetryHandler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), () => _now, (d, _) => { _now += d; return Task.CompletedTask; });
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });
        var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(factories), new V2ScriptDispatcher(factories) }, _cache);

        return new HalibutMachineExecutionStrategy(factories, new CalamariPayloadBuilder(new Moq.Mock<IYamlNuGetPacker>().Object), observer, new HalibutSetting(), _breakers, inFlightStore: _store, rpcRetry: retry, dispatcherSelector: selector, capabilitiesRefresher: _refresher);
    }

    private ScriptExecutionRequest Request() => new()
    {
        Machine = _machine,
        ScriptBody = "echo hi",
        ExecutionMode = ExecutionMode.DirectScript,
        Syntax = ScriptSyntax.Bash,
        ReleaseVersion = "1.0.0",
        ServerTaskId = 777,
        StepId = 11,
        ActionId = 22,
        StepName = "step",
        ActionName = "action",
        Timeout = TimeSpan.FromMinutes(7),
        Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
    };

    /// <summary>A V1 agent every call to which fails: for pinning what a failing V1 probe after a downgrade must (not) mean.</summary>
    private sealed class ThrowingV1Agent : IAsyncScriptService
    {
        private readonly Exception _failure;

        public ThrowingV1Agent(Exception failure) => _failure = failure;

        public int StartCalls { get; private set; }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
        {
            StartCalls++;
            throw _failure;
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) => throw _failure;

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => throw _failure;

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => throw _failure;
    }
}
