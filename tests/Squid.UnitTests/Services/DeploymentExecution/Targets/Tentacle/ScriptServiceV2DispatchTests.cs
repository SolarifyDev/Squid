using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
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
/// Drives the real <see cref="HalibutMachineExecutionStrategy"/> + real selector, dispatchers, sessions and
/// observer against hand-written V1 and V2 agents, to pin how a script's protocol version is chosen
/// (only from the advertised capabilities), persisted with the in-flight record, and honoured on resume —
/// and that the generic machinery (record-before-RPC, re-attach, breaker, cancel decision, retry budgets)
/// behaves for V2 exactly as the V1 pinning tests require of V1. Distinct values per same-typed field
/// (machine 42 / step 11 / action 22 / task 777) make a swapped argument observable.
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class ScriptServiceV2DispatchTests
{
    private const int MachineId = 42;
    private const int StepId = 11;
    private const int ActionId = 22;
    private const int ServerTaskId = 777;

    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(7);
    private static readonly DispatchSlot Slot = new(MachineId, StepId, ActionId);

    private readonly List<string> _events = new();
    private readonly FakeV1Agent _v1;
    private readonly FakeV2Agent _v2;
    private readonly FakeClientFactories _factories;
    private readonly VersionedFakeStore _store;
    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();
    private readonly MachineCircuitBreakerRegistry _breakers = new(new HalibutSetting());
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Machine _machine = new() { Id = MachineId, Name = "agent-42", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-v2","Thumbprint":"AABBCCDD"}""" };

    public ScriptServiceV2DispatchTests()
    {
        _v1 = new FakeV1Agent(_events);
        _v2 = new FakeV2Agent(_events);
        _factories = new FakeClientFactories(_v1, _v2);
        _store = new VersionedFakeStore(_events);
    }

    private void AdvertiseV2() => _cache.Store(MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 });

    private void AdvertiseV1Only() => _cache.Store(MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "1.0.0", new[] { "IScriptService/v1" });

    // ── Choosing and persisting the version ──────────────────────────────────

    [Fact]
    public async Task AdvertisedV2_FreshDispatch_UsesV2_AndRecordsTheVersionBeforeStart()
    {
        AdvertiseV2();

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        var fresh = _v2.StartCommands.ShouldHaveSingleItem().ScriptTicket.TaskId;

        Normalised(fresh).ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "Store.Record:42/11/22:FRESH:V2",
            "V2.Start:FRESH",
            "V2.GetStatus:FRESH:0",
            "V2.GetStatus:FRESH:0",
            "V2.Complete:FRESH:0",
            "Store.Clear:42/11/22"
        });
        _factories.V1Opened.ShouldBe(0, "a V2 dispatch must not touch the V1 service");
    }

    [Fact]
    public async Task ColdCache_FreshDispatch_UsesV1_AndNeverOpensAV2Client()
    {
        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        var fresh = _v1.StartCommands.ShouldHaveSingleItem().ScriptTicket.TaskId;

        _events.ShouldContain($"Store.Record:42/11/22:{fresh}:V1");
        _factories.V2Opened.ShouldBe(0, "choosing V1 is decided from the advertised capabilities, never by calling V2 and catching the failure");
    }

    [Fact]
    public async Task AdvertisedV1Only_FreshDispatch_UsesV1()
    {
        AdvertiseV1Only();

        await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        _v1.StartCalls.ShouldBe(1);
        _v2.StartCalls.ShouldBe(0);
        _factories.V2Opened.ShouldBe(0);
    }

    [Fact]
    public async Task V2Start_SendsEveryFieldOfTheSpec_ByName()
    {
        AdvertiseV2();
        var request = DirectRequest();

        await Strategy().ExecuteScriptAsync(request, CancellationToken.None);

        var cmd = _v2.StartCommands.ShouldHaveSingleItem();

        cmd.ScriptTicket.TaskId.ShouldMatch("^[a-f0-9]{32}$");
        cmd.ScriptBody.ShouldBe("echo v2");
        cmd.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        cmd.ScriptIsolationMutexTimeout.ShouldBe(ScriptTimeout);
        cmd.IsolationMutexName.ShouldBe("squid/machine/42");
        cmd.TaskId.ShouldBe("777");
        cmd.DurationToWaitForScriptToFinish.ShouldBe(TimeSpan.Zero);
        cmd.Arguments.ShouldBeEmpty();
        cmd.ScriptSyntax.ShouldBe(ScriptType.Python);
        cmd.TargetNamespace.ShouldBe("ns-v2");
        cmd.Files.Select(f => f.Name).ShouldBe(new[] { "variables.json", "sensitiveVariables.json" });
    }

    // ── Resume: the recorded version wins ────────────────────────────────────

    [Fact]
    public async Task Resume_RecordedV2_ReattachesOverV2_WithTheSameTicket_NoFreshStart()
    {
        AdvertiseV2();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.Know("OLD");

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _v2.StartCalls.ShouldBe(0, "a still-running recorded script must never be dispatched twice");
        _events.ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "V2.GetStatus:OLD:0",
            "V2.GetStatus:OLD:0",
            "V2.Complete:OLD:0",
            "Store.Clear:42/11/22"
        });
    }

    [Fact]
    public async Task Resume_RecordedV2_AgentNowAdvertisesOnlyV1_StillProbesOverV2_NeverV1()
    {
        AdvertiseV1Only();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.Know("OLD");

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _events.ShouldNotContain(e => e.StartsWith("V1."), "the script lives in the agent's V2 state; a V1 probe would read 'unknown' and re-run it");
        _v1.StartCalls.ShouldBe(0);
        _v2.StartCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(typeof(HalibutClientException))]   // what an agent that cannot speak V2 answers: classified transient
    [InlineData(typeof(InvalidOperationException))] // anything else: still not evidence that the script did not run
    public async Task Resume_RecordedV2_AgentCannotAnswerV2_PreservesPointer_NoFreshDispatch_NoV1Fallback(Type failure)
    {
        AdvertiseV1Only();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = (Exception)Activator.CreateInstance(failure, "agent cannot speak V2");

        await Should.ThrowAsync<Exception>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _events.ShouldNotContain(e => e.StartsWith("Store.Clear") || e.StartsWith("Store.Record"), "the pointer must survive so a later resume can still find the script");
        _v1.StartCalls.ShouldBe(0, "falling back to V1 / dispatching fresh would run a possibly-finished script a second time");
        _v2.StartCalls.ShouldBe(0);
        _events.ShouldNotContain(e => e.StartsWith("V1."));
    }

    [Fact]
    public async Task Resume_LegacyRecordWithoutAVersion_ReattachesOverV1_EvenWhenTheAgentAdvertisesV2()
    {
        AdvertiseV2();
        _store.Seed(Slot, "OLD", version: null);
        _v1.Know("OLD");

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _events.ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "V1.GetStatus:OLD:0",
            "V1.GetStatus:OLD:0",
            "V1.Complete:OLD:0",
            "Store.Clear:42/11/22"
        });
        _factories.V2Opened.ShouldBe(1, "only the session opened for the NEW dispatch's candidate version; the record is probed over V1");
        _v2.StartCalls.ShouldBe(0);
    }

    // ── Resume: explicit V2 outcomes ─────────────────────────────────────────

    [Fact]
    public async Task Resume_V2UnknownTicket_AgentNeverRanIt_ClearsThenDispatchesFresh()
    {
        AdvertiseV2();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);

        await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        var fresh = _v2.StartCommands.ShouldHaveSingleItem().ScriptTicket.TaskId;

        fresh.ShouldNotBe("OLD");
        Normalised(fresh).Take(5).ShouldBe(new[]
        {
            "Store.TryGet:42/11/22",
            "V2.GetStatus:OLD:0",
            "Store.Clear:42/11/22",
            "Store.Record:42/11/22:FRESH:V2",
            "V2.Start:FRESH"
        });
    }

    [Fact]
    public async Task Resume_V2UnknownTicket_ButTheAgentRemembersItCompleted_ReportsThatOutcome_DoesNotRerun()
    {
        // Complete released the ticket, then the server died before clearing its pointer. The agent answers
        // GetStatus for the released ticket from its completion tombstone.
        AdvertiseV2();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.Tombstones["OLD"] = (ScriptStateV2.Completed, 3);

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        _v2.StartCalls.ShouldBe(0, "the agent remembers this ticket completed — running it again would duplicate it");
        result.ExitCode.ShouldBe(3);
        result.Success.ShouldBeFalse();
        _events.ShouldNotContain(e => e.StartsWith("V2.Cancel"), "a resume must never send CancelScript to learn state");
    }

    [Fact]
    public async Task Resume_V2ResultLost_FailsTheActionWithAnExplicitMessage_NeverRedispatches()
    {
        AdvertiseV2();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.Know("OLD", ScriptStateV2.ResultLost);

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
        result.StderrLines.ShouldHaveSingleItem().ShouldContain("lost the result of script OLD");
        result.StderrLines[0].ShouldContain("did NOT run it again");
        result.BuildErrorSummary().ShouldContain("Check the target");
        _v2.StartCalls.ShouldBe(0);
        _v1.StartCalls.ShouldBe(0);
        _events.ShouldNotContain(e => e.StartsWith("V2.Complete"));
        _events.ShouldContain("Store.Clear:42/11/22", "the outcome is definitive (failed), so the pointer is released");
    }

    [Fact]
    public async Task Resume_V2ScriptThatReallyExitedMinusOne_IsACompletedResult_NotLost_NotRedispatched()
    {
        AdvertiseV2();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.Know("OLD", ScriptStateV2.Completed, ScriptExitCodes.UnknownResult);

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        _v2.StartCalls.ShouldBe(0, "exit -1 is a real exit code on V2, not the unknown-ticket sentinel V1 has to treat it as");
        result.ExitCode.ShouldBe(-1);
        result.Success.ShouldBeFalse();
        result.StderrLines.ShouldBeEmpty("a normal completion carries no lost-result message");
        _events.ShouldContain(e => e.StartsWith("V2.Complete:OLD"));
    }

    [Fact]
    public async Task FreshV2Script_ExitingMinusOne_IsCompletedMinusOne_AndStartedExactlyOnce()
    {
        AdvertiseV2();
        _v2.StartedScriptExitCode = -1;

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.ExitCode.ShouldBe(-1);
        result.Success.ShouldBeFalse();
        _v2.StartCalls.ShouldBe(1);
    }

    // ── Start retries ────────────────────────────────────────────────────────

    [Fact]
    public async Task V2Start_LostResponse_IsRetriedWithinTheBudget_AndTheScriptRunsOnce()
    {
        AdvertiseV2();
        _v2.StartFailuresAfterRunning.Enqueue(new HalibutClientException("response lost"));

        var result = await Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _v2.StartCalls.ShouldBe(2, "V2 Start is idempotent per ticket, so a lost response is retried");
        _v2.StartCommands.Count.ShouldBe(1, "the agent saw one script: the retry carried the same ticket and did not run it again");
        _events.Count(e => e.StartsWith("Store.Record")).ShouldBe(1);
    }

    [Fact]
    public async Task V2Start_FailingBeyondTheBudget_Rethrows_AndChargesTheBreakerOnce()
    {
        AdvertiseV2();
        for (var i = 0; i < 50; i++) _v2.StartFailuresBeforeRunning.Enqueue(new HalibutClientException("down"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _v2.StartCalls.ShouldBeGreaterThan(1);
        _v2.StartCalls.ShouldBeLessThan(50, "retries stop when the time budget is spent");
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1, "retries never touch the breaker: one failure per dispatch");
    }

    [Fact]
    public async Task V1Start_Failure_IsNotRetried()
    {
        _v1.StartFailures.Enqueue(new HalibutClientException("response lost"));

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _v1.StartCalls.ShouldBe(1, "V1 StartScript is not idempotent: a retry after a lost response could run the script twice");
    }

    // ── Generic machinery, same as V1 ────────────────────────────────────────

    [Fact]
    public async Task V2_TransientFailureDuringObservation_PreservesPointer_AndChargesBreakerOnce()
    {
        AdvertiseV2();
        _v2.GetStatusFailure = new HalibutClientException("connection reset");

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _events.ShouldContain(e => e.StartsWith("Store.Record") && e.EndsWith(":V2"));
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"), "a throw leaves the pointer so a resume re-attaches");
        _events.ShouldNotContain(e => e.StartsWith("V2.Complete"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Fact]
    public async Task V2_Reattach_ProbeTransientFailure_PausesAndPreservesPointer()
    {
        AdvertiseV2();
        _store.Seed(Slot, "OLD", ScriptProtocolVersion.V2);
        _v2.GetStatusFailure = new HalibutClientException("connection reset");

        await Should.ThrowAsync<HalibutClientException>(() => Strategy().ExecuteScriptAsync(DirectRequest(), CancellationToken.None));

        _v2.StartCalls.ShouldBe(0);
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear") || e.StartsWith("Store.Record"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, true)]   // no decision: cancel on every interruption
    [InlineData(true, true)]    // the outcome is final: cancel the remote script
    [InlineData(true, false)]   // the task is only pausing: leave it running
    public async Task V2_CancellationDuringObservation_CancelsRemoteScriptPerDecision_PreservesPointer(bool hasDecision, bool decisionValue)
    {
        AdvertiseV2();
        using var cts = new CancellationTokenSource();
        _v2.OnGetStatus = () => cts.Cancel();

        var request = DirectRequest();
        request.ShouldCancelRemoteScript = hasDecision ? () => decisionValue : null;

        await Should.ThrowAsync<OperationCanceledException>(() => Strategy().ExecuteScriptAsync(request, cts.Token));

        _events.Any(e => e.StartsWith("V2.Cancel")).ShouldBe(!hasDecision || decisionValue);
        _events.ShouldNotContain(e => e.StartsWith("Store.Clear"));
        _breakers.GetOrCreate(MachineId).ConsecutiveFailures.ShouldBe(0, "cancellation is not an agent failure");
    }

    [Fact]
    public async Task UnsupportedSyntax_OverV2_ThrowsBeforeAnyStoreOrStartRpc()
    {
        AdvertiseV2();
        var request = DirectRequest();
        request.Syntax = (ScriptSyntax)999;

        await Should.ThrowAsync<InvalidOperationException>(() => Strategy().ExecuteScriptAsync(request, CancellationToken.None));

        _events.ShouldBeEmpty("the command is built before the re-attach probe, the in-flight record and StartScript");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private HalibutMachineExecutionStrategy Strategy()
    {
        var retry = new RpcRetryHandler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), () => _now, (d, _) => { _now += d; return Task.CompletedTask; });
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });
        var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(_factories), new V2ScriptDispatcher(_factories) }, _cache);

        return new HalibutMachineExecutionStrategy(_factories, new CalamariPayloadBuilder(new Moq.Mock<IYamlNuGetPacker>().Object), observer, new HalibutSetting(), _breakers, inFlightStore: _store, rpcRetry: retry, dispatcherSelector: selector);
    }

    private ScriptExecutionRequest DirectRequest() => new()
    {
        Machine = _machine,
        ScriptBody = "echo v2",
        ExecutionMode = ExecutionMode.DirectScript,
        Syntax = ScriptSyntax.Python,
        TargetNamespace = "ns-v2",
        ReleaseVersion = "1.0.0",
        ServerTaskId = ServerTaskId,
        StepId = StepId,
        ActionId = ActionId,
        StepName = "step",
        ActionName = "action",
        Timeout = ScriptTimeout,
        Variables = new List<Message.Models.Deployments.Variable.VariableDto>
        {
            new() { Name = "Plain", Value = "v", IsSensitive = false },
            new() { Name = "Secret", Value = "s", IsSensitive = true }
        }
    };

    private string[] Normalised(string freshTicket) => _events.Select(e => e.Replace(freshTicket, "FRESH")).ToArray();
}
