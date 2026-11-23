using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// The V2 dispatcher's own surface: the start command it builds (every field distinct so a swap shows),
/// the mapping of every V2 state / outcome onto the neutral ones (nothing folded into an exit code, a value
/// the contract forbids is a protocol violation), the probe that looks past GetStatus's blind spot, and the
/// retry rules (V2 Start retried, GetStatus / Cancel / Complete on the same budgets V1 uses).
/// </summary>
public class V2ScriptDispatcherTests
{
    private static readonly ServiceEndPoint Endpoint = new("poll://x/", "AABB", new HalibutTimeoutsAndLimits());

    private readonly List<string> _events = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Start command mapping ────────────────────────────────────────────────

    [Fact]
    public void BuildCommand_MapsEverySpecField_ToItsOwnProperty()
    {
        var files = new[] { new ScriptFile("f1.txt", DataStream.FromString("one"), null), new ScriptFile("f2.txt", DataStream.FromString("two"), "pw") };
        var spec = new ScriptDispatchSpec
        {
            Ticket = new ScriptTicket("ticket-1"),
            ScriptBody = "body-2",
            Files = files,
            Timeout = TimeSpan.FromMinutes(7),
            IsolationMutexName = "mutex-3",
            ServerTaskId = 777,
            Syntax = ScriptSyntax.Python,
            TargetNamespace = "ns-4"
        };

        var cmd = V2ScriptDispatcher.BuildCommand(spec);

        cmd.ScriptTicket.TaskId.ShouldBe("ticket-1");
        cmd.ScriptBody.ShouldBe("body-2");
        cmd.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        cmd.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(7), "the script timeout rides in the mutex-timeout slot exactly as in V1");
        cmd.IsolationMutexName.ShouldBe("mutex-3");
        cmd.TaskId.ShouldBe("777", "the correlation task id, not the mutex name");
        cmd.DurationToWaitForScriptToFinish.ShouldBe(TimeSpan.Zero);
        cmd.Arguments.ShouldBeEmpty();
        cmd.ScriptSyntax.ShouldBe(ScriptType.Python);
        cmd.TargetNamespace.ShouldBe("ns-4");
        cmd.Labels.ShouldBeNull();
        cmd.Scripts.ShouldBeEmpty();
        cmd.Files.Select(f => f.Name).ShouldBe(new[] { "f1.txt", "f2.txt" });
        cmd.Files[1].EncryptionPassword.ShouldBe("pw");
    }

    [Fact]
    public void BuildCommand_SendsTheSameValuesAsV1_ForTheSameSpec()
    {
        var spec = new ScriptDispatchSpec { Ticket = new ScriptTicket("t"), ScriptBody = "b", Files = Array.Empty<ScriptFile>(), Timeout = TimeSpan.FromMinutes(9), IsolationMutexName = "m", ServerTaskId = 5, Syntax = ScriptSyntax.PowerShell, TargetNamespace = "n" };
        StartScriptCommand v1 = null;
        var agent = new Moq.Mock<IAsyncScriptService>();
        agent.Setup(a => a.StartScriptAsync(It.IsAny<StartScriptCommand>())).Callback<StartScriptCommand>(c => v1 = c).ReturnsAsync(new ScriptStatusResponse(spec.Ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));
        new V1ScriptDispatcher(new Moq.Mock<Squid.Core.Services.DeploymentExecution.Transport.IHalibutClientFactory>().Object).PrepareStart(agent.Object, spec)().Wait();

        var v2 = V2ScriptDispatcher.BuildCommand(spec);

        v2.ScriptIsolationMutexTimeout.ShouldBe(v1.ScriptIsolationMutexTimeout);
        v2.IsolationMutexName.ShouldBe(v1.IsolationMutexName);
        v2.TaskId.ShouldBe(v1.TaskId);
        v2.DurationToWaitForScriptToFinish.ShouldBe(v1.DurationToWaitForScriptToFinish);
        v2.ScriptSyntax.ShouldBe(v1.ScriptSyntax);
        v2.TargetNamespace.ShouldBe(v1.TargetNamespace);
        v2.Isolation.ShouldBe(v1.Isolation);
    }

    [Fact]
    public void PrepareStart_UnsupportedSyntax_ThrowsBeforeAnyRpc()
    {
        var session = OpenSession(new Moq.Mock<IAsyncScriptServiceV2>(Moq.MockBehavior.Strict).Object);

        Should.Throw<InvalidOperationException>(() => session.PrepareStart(Spec() with { Syntax = (ScriptSyntax)999 }));
    }

    [Fact]
    public async Task PrepareStart_PerformsNoIo_UntilTheDelegateRuns()
    {
        var agent = new FakeV2Agent(_events);
        var session = OpenSession(agent);

        var start = session.PrepareStart(Spec());

        agent.StartCalls.ShouldBe(0);

        await start();

        agent.StartCalls.ShouldBe(1);
    }

    // ── State / outcome mapping ──────────────────────────────────────────────

    [Theory]
    [InlineData(ScriptStateV2.Pending, AgentScriptState.Pending)]
    [InlineData(ScriptStateV2.Running, AgentScriptState.Running)]
    [InlineData(ScriptStateV2.Completed, AgentScriptState.Completed)]
    [InlineData(ScriptStateV2.UnknownTicket, AgentScriptState.UnknownTicket)]
    [InlineData(ScriptStateV2.ResultLost, AgentScriptState.ResultLost)]
    public void EveryV2State_MapsToItsOwnNeutralState_KeepingTicketExitCodeLogsAndCursor(ScriptStateV2 wire, AgentScriptState expected)
    {
        var logs = new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "line", DateTimeOffset.UtcNow) };

        var neutral = V2ScriptAgentClient.ToNeutral(new ScriptStatusResponseV2(new ScriptTicket("t"), wire, 12, logs, 34));

        neutral.State.ShouldBe(expected);
        neutral.Ticket.TaskId.ShouldBe("t");
        neutral.ExitCode.ShouldBe(12);
        neutral.Logs.ShouldBeSameAs(logs);
        neutral.NextLogSequence.ShouldBe(34);
    }

    [Theory]
    [InlineData(ScriptStateV2.Unspecified)]
    [InlineData((ScriptStateV2)99)]
    public void AStateTheContractForbids_IsAProtocolViolation_NotGuessed(ScriptStateV2 wire)
        => Should.Throw<AgentProtocolViolationException>(() => V2ScriptAgentClient.ToNeutral(new ScriptStatusResponseV2(new ScriptTicket("t"), wire, 0, new List<ProcessOutput>(), 0)));

    [Fact]
    public void ANullLogList_BecomesEmpty()
        => V2ScriptAgentClient.ToNeutral(new ScriptStatusResponseV2(new ScriptTicket("t"), ScriptStateV2.Running, 0, null, 0)).Logs.ShouldBeEmpty();

    [Theory]
    [InlineData(CompleteScriptOutcomeV2.Completed, AgentCompleteOutcome.Completed)]
    [InlineData(CompleteScriptOutcomeV2.AlreadyCompleted, AgentCompleteOutcome.AlreadyCompleted)]
    [InlineData(CompleteScriptOutcomeV2.UnknownTicket, AgentCompleteOutcome.UnknownTicket)]
    [InlineData(CompleteScriptOutcomeV2.NotYetComplete, AgentCompleteOutcome.NotYetComplete)]
    public void EveryV2CompleteOutcome_MapsToItsOwnNeutralOutcome(CompleteScriptOutcomeV2 wire, AgentCompleteOutcome expected)
    {
        var final = new ScriptStatusResponseV2(new ScriptTicket("t"), ScriptStateV2.Completed, 3, new List<ProcessOutput>(), 1);

        var neutral = V2ScriptAgentClient.ToNeutral(new CompleteScriptResponseV2(new ScriptTicket("t"), wire, final));

        neutral.Outcome.ShouldBe(expected);
        neutral.Final.ExitCode.ShouldBe(3);
    }

    [Theory]
    [InlineData(CompleteScriptOutcomeV2.Completed)]
    [InlineData(CompleteScriptOutcomeV2.NotYetComplete)]
    [InlineData(CompleteScriptOutcomeV2.Unspecified)]
    public void ACompleteOutcomeTheContractForbids_OrOneMissingItsFinalStatus_IsAProtocolViolation(CompleteScriptOutcomeV2 wire)
        => Should.Throw<AgentProtocolViolationException>(() => V2ScriptAgentClient.ToNeutral(new CompleteScriptResponseV2(new ScriptTicket("t"), wire, null)));

    [Theory]
    [InlineData(CompleteScriptOutcomeV2.AlreadyCompleted)]
    [InlineData(CompleteScriptOutcomeV2.UnknownTicket)]
    public void AlreadyCompletedAndUnknown_NeedNoFinalStatus(CompleteScriptOutcomeV2 wire)
        => V2ScriptAgentClient.ToNeutral(new CompleteScriptResponseV2(new ScriptTicket("t"), wire, null)).Final.ShouldBeNull();

    // ── Unknown-ticket recognition ───────────────────────────────────────────

    [Theory]
    [InlineData(AgentScriptState.UnknownTicket, 0, true)]
    [InlineData(AgentScriptState.Completed, -1, false)]   // a script that really exited -1
    [InlineData(AgentScriptState.Completed, 0, false)]
    [InlineData(AgentScriptState.ResultLost, 0, false)]   // may have run: handled separately, never "fresh"
    [InlineData(AgentScriptState.Running, 0, false)]
    [InlineData(AgentScriptState.Pending, 0, false)]
    public void V2_IsUnknownTicket_OnlyForTheExplicitUnknownState(AgentScriptState state, int exit, bool expected)
        => OpenSession(new FakeV2Agent(_events)).IsUnknownTicket(new AgentScriptStatus(new ScriptTicket("t"), state, exit, new List<ProcessOutput>(), 0)).ShouldBe(expected);

    [Fact]
    public void V2_IsUnknownTicket_NullProbe_IsUnknown() => OpenSession(new FakeV2Agent(_events)).IsUnknownTicket(null).ShouldBeTrue();

    [Fact]
    public void V2_ProbeFailure_IsNeverEvidenceOfAnUnknownTicket_ButV1Keeps_ItsHistoricFallback()
    {
        OpenSession(new FakeV2Agent(_events)).ProbeFailureMeansUnknownTicket.ShouldBeFalse();

        var v1 = new V1ScriptDispatcher(new FakeClientFactories(new FakeV1Agent(_events), new FakeV2Agent(_events))).Open(Endpoint, Context());

        v1.ProbeFailureMeansUnknownTicket.ShouldBeTrue();
    }

    [Theory]
    [InlineData(AgentScriptState.Completed, -1, true)]    // V1: the sentinel is the only unknown signal
    [InlineData(AgentScriptState.Completed, 0, false)]
    [InlineData(AgentScriptState.Running, 0, false)]
    public void V1_IsUnknownTicket_StaysTheMinusOneSentinel_OnTheNeutralStatus(AgentScriptState state, int exit, bool expected)
    {
        var v1 = new V1ScriptDispatcher(new FakeClientFactories(new FakeV1Agent(_events), new FakeV2Agent(_events))).Open(Endpoint, Context());

        v1.IsUnknownTicket(new AgentScriptStatus(new ScriptTicket("t"), state, exit, new List<ProcessOutput>(), 0)).ShouldBe(expected);
    }

    // ── Probe ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_OfAKnownTicket_IsASingleGetStatus_NoCancel()
    {
        var agent = new FakeV2Agent(_events);
        agent.Know("t");

        var probe = await OpenSession(agent).ProbeAsync(new ScriptTicket("t"));

        probe.State.ShouldBe(AgentScriptState.Running);
        _events.ShouldBe(new[] { "V2.GetStatus:t:0" });
    }

    [Fact]
    public async Task Probe_OfAReleasedTicket_IsAnsweredByGetStatusAlone_NeverByACancel()
    {
        var agent = new FakeV2Agent(_events);
        agent.Tombstones["t"] = (ScriptStateV2.Completed, 5);

        var probe = await OpenSession(agent).ProbeAsync(new ScriptTicket("t"));

        probe.State.ShouldBe(AgentScriptState.Completed);
        probe.ExitCode.ShouldBe(5);
        _events.ShouldBe(new[] { "V2.GetStatus:t:0" });
    }

    [Fact]
    public async Task Probe_UnknownToGetStatus_IsUnknown_WithNoFurtherCall()
    {
        var probe = await OpenSession(new FakeV2Agent(_events)).ProbeAsync(new ScriptTicket("t"));

        probe.State.ShouldBe(AgentScriptState.UnknownTicket);
        _events.ShouldBe(new[] { "V2.GetStatus:t:0" });
    }

    // ── Retry rules ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_TransientBlip_IsRetried_WithTheSameLogCursor()
    {
        var inner = new FlakyClient { StatusFailures = 2 };
        var client = Retrying(inner);

        var status = await client.GetStatusAsync(new ScriptTicket("t"), 41);

        status.State.ShouldBe(AgentScriptState.Running);
        inner.Calls.ShouldBe(new[] { "status:41", "status:41", "status:41" });
    }

    [Fact]
    public async Task GetStatus_BeyondTheBudget_RethrowsTheOriginalException()
    {
        var inner = new FlakyClient { StatusFailures = 1000 };

        await Should.ThrowAsync<HalibutClientException>(() => Retrying(inner, statusSeconds: 3).GetStatusAsync(new ScriptTicket("t"), 0));

        inner.Calls.Count.ShouldBeGreaterThan(1);
        inner.Calls.Count.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task GetStatus_BudgetIsCappedByTheScriptsRemainingTimeout()
    {
        var inner = new FlakyClient { StatusFailures = 1000 };

        await Should.ThrowAsync<HalibutClientException>(() => Retrying(inner, scriptTimeout: TimeSpan.Zero).GetStatusAsync(new ScriptTicket("t"), 0));

        inner.Calls.Count.ShouldBe(1, "a script past its deadline gets no retries: retrying must not extend the per-script timeout");
    }

    [Fact]
    public async Task GetStatus_NonTransientFailure_IsNotRetried()
    {
        var inner = new FlakyClient { StatusFailures = 1, Failure = new InvalidOperationException("boom") };

        await Should.ThrowAsync<InvalidOperationException>(() => Retrying(inner).GetStatusAsync(new ScriptTicket("t"), 0));

        inner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_IsRetriedOnItsOwnBudget_EvenAfterTheCallerCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var inner = new FlakyClient { CancelFailures = 2 };

        var status = await Retrying(inner, ct: cts.Token).CancelAsync(new ScriptTicket("t"), 0);

        status.State.ShouldBe(AgentScriptState.Completed);
        inner.Calls.Count(c => c.StartsWith("cancel")).ShouldBe(3, "cancel normally runs AFTER the caller was cancelled, so its retries ignore the caller token");
    }

    [Fact]
    public async Task Cancel_BeyondItsBudget_Rethrows()
    {
        var inner = new FlakyClient { CancelFailures = 1000 };

        await Should.ThrowAsync<HalibutClientException>(() => Retrying(inner, cancelSeconds: 2).CancelAsync(new ScriptTicket("t"), 0));

        inner.Calls.Count.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task Complete_TransientBlip_IsRetried_OnTheCompleteBudget()
    {
        var inner = new FlakyClient { CompleteFailures = 2 };

        var completion = await Retrying(inner).CompleteAsync(new ScriptTicket("t"), 7);

        completion.Outcome.ShouldBe(AgentCompleteOutcome.Completed);
        inner.Calls.ShouldBe(new[] { "complete:7", "complete:7", "complete:7" });
    }

    [Fact]
    public async Task Complete_BeyondItsBudget_Rethrows_AndDoesNotUseAnyOtherBudget()
    {
        var inner = new FlakyClient { CompleteFailures = 1000 };

        await Should.ThrowAsync<HalibutClientException>(() => Retrying(inner, statusSeconds: 3600, completeSeconds: 2).CompleteAsync(new ScriptTicket("t"), 0));

        inner.Calls.Count.ShouldBeLessThan(10, "Complete has its own short best-effort budget, not the long status one");
    }

    [Fact]
    public async Task StopRetrying_EndsTheRetriesImmediately()
    {
        var inner = new FlakyClient { StatusFailures = 1000 };
        var client = Retrying(inner);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        client.BindStopRetrying(stop.Token);

        await Should.ThrowAsync<HalibutClientException>(() => client.GetStatusAsync(new ScriptTicket("t"), 0));

        inner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_FailingNonTransiently_IsNotRetried()
    {
        var agent = new FakeV2Agent(_events);
        agent.StartFailuresBeforeRunning.Enqueue(new InvalidOperationException("bad request"));

        await Should.ThrowAsync<InvalidOperationException>(() => OpenSession(agent).PrepareStart(Spec())());

        agent.StartCalls.ShouldBe(1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ScriptSessionContext Context() => new(Retry(), TimeSpan.FromMinutes(5), CancellationToken.None, new HalibutScriptObserver());

    private RpcRetryHandler Retry(int statusSeconds = 120, int completeSeconds = 30, int cancelSeconds = 20)
        => new(TimeSpan.FromSeconds(statusSeconds), TimeSpan.FromSeconds(completeSeconds), TimeSpan.FromSeconds(cancelSeconds), () => _now, (d, ct) => { ct.ThrowIfCancellationRequested(); _now += d; return Task.CompletedTask; });

    private RetryingScriptAgentClientHandle Retrying(FlakyClient inner, TimeSpan? scriptTimeout = null, int statusSeconds = 120, int completeSeconds = 30, int cancelSeconds = 20, CancellationToken ct = default)
        => new(inner, Retry(statusSeconds, completeSeconds, cancelSeconds), scriptTimeout ?? TimeSpan.FromMinutes(30), ct);

    private IScriptAgentSession OpenSession(IAsyncScriptServiceV2 agent)
        => new V2ScriptDispatcher(new FakeClientFactories(new FakeV1Agent(_events), agent)).Open(Endpoint, Context());

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

    /// <summary>The internal retrying client behind a test-visible handle.</summary>
    private sealed class RetryingScriptAgentClientHandle : IScriptAgentClient, IStopRetryingAware
    {
        private readonly RetryingScriptAgentClient _client;

        public RetryingScriptAgentClientHandle(IScriptAgentClient inner, RpcRetryHandler retry, TimeSpan scriptTimeout, CancellationToken ct)
            => _client = new RetryingScriptAgentClient(inner, retry, scriptTimeout, ct);

        public void BindStopRetrying(CancellationToken stopRetrying) => _client.BindStopRetrying(stopRetrying);

        public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence) => _client.GetStatusAsync(ticket, lastLogSequence);

        public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence) => _client.CancelAsync(ticket, lastLogSequence);

        public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence) => _client.CompleteAsync(ticket, lastLogSequence);
    }

    private sealed class FlakyClient : IScriptAgentClient
    {
        public int StatusFailures { get; set; }
        public int CancelFailures { get; set; }
        public int CompleteFailures { get; set; }
        public Exception Failure { get; set; } = new HalibutClientException("connection reset");
        public List<string> Calls { get; } = new();

        public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
        {
            Calls.Add($"status:{lastLogSequence}");

            if (StatusFailures-- > 0) throw Failure;

            return Task.FromResult(new AgentScriptStatus(ticket, AgentScriptState.Running, 0, new List<ProcessOutput>(), lastLogSequence));
        }

        public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence)
        {
            Calls.Add($"cancel:{lastLogSequence}");

            if (CancelFailures-- > 0) throw Failure;

            return Task.FromResult(new AgentScriptStatus(ticket, AgentScriptState.Completed, 0, new List<ProcessOutput>(), lastLogSequence));
        }

        public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
        {
            Calls.Add($"complete:{lastLogSequence}");

            if (CompleteFailures-- > 0) throw Failure;

            return Task.FromResult(new AgentScriptCompletion(AgentCompleteOutcome.Completed, new AgentScriptStatus(ticket, AgentScriptState.Completed, 0, new List<ProcessOutput>(), lastLogSequence)));
        }
    }
}
