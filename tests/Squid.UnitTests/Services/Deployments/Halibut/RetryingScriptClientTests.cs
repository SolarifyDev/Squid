using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Diagnostics;
using Halibut.Exceptions;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Halibut;

/// <summary>
/// Pins the observer + <see cref="RetryingScriptClient"/> pair against a scripted agent: a brief
/// connection drop is absorbed (full logs, real exit code), an outage beyond the budget surfaces the
/// ORIGINAL exception (so the runner's transient-pause path is unchanged), Cancel is retried only when
/// it was decided to be sent, and the non-idempotent CompleteScript never turns a finished script into
/// an "unknown" result after a lost response.
/// </summary>
public class RetryingScriptClientTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromMinutes(30);

    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly ScriptTicket _ticket = new("t1");
    private readonly Machine _machine = new() { Name = "agent" };
    private readonly HalibutScriptObserver _observer = new(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });

    private RpcRetryHandler Handler(int statusSeconds = 120, int completeSeconds = 30)
        => new(TimeSpan.FromSeconds(statusSeconds), TimeSpan.FromSeconds(completeSeconds), () => _now, (d, ct) => { ct.ThrowIfCancellationRequested(); _now += d; return Task.CompletedTask; });

    private RetryingScriptClient Retrying(Agent agent, TimeSpan? scriptTimeout = null, int statusSeconds = 120, int completeSeconds = 30, CancellationToken ct = default)
        => new(agent, Handler(statusSeconds, completeSeconds), scriptTimeout ?? Plenty, ct);

    private static ScriptStatusResponse Status(ScriptTicket t, ProcessState state, int exit, long next, params string[] lines)
        => new(t, state, exit, lines.Select(l => new ProcessOutput(ProcessOutputSource.StdOut, l, DateTimeOffset.UtcNow)).ToList(), next);

    private Task<ScriptExecutionResult> ObserveAsync(IAsyncScriptService client, CancellationToken ct = default, RemoteScriptCancelDecision decision = null)
        => _observer.ObserveAndCompleteAsync(_machine, client, _ticket, Plenty, ct, null, null, null, null, decision);

    // ── GetStatus ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task GetStatusBlip_WithinBudget_ScriptCompletesWithFullLogs(int failuresBeforeRecovery)
    {
        var agent = new Agent
        {
            OnGetStatus = n => n switch
            {
                1 => Status(_ticket, ProcessState.Running, 0, 1, "line-a"),
                <= 5 when n - 1 <= failuresBeforeRecovery => throw new HalibutClientException("connection reset"),
                _ => Status(_ticket, ProcessState.Complete, 0, 2, "line-b")
            },
            OnComplete = _ => Status(_ticket, ProcessState.Complete, 0, 2)
        };

        var result = await ObserveAsync(Retrying(agent));

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldBe(new[] { "line-a", "line-b" });
        agent.GetStatusCalls.ShouldBe(2 + failuresBeforeRecovery);
        agent.CancelCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatusRetry_RequestsTheSameLogRange_SoNothingIsLostOrDuplicated()
    {
        var agent = new Agent
        {
            OnGetStatus = n => n switch
            {
                1 => Status(_ticket, ProcessState.Running, 0, 7, "a"),
                2 => throw new HalibutClientException("drop"),
                _ => Status(_ticket, ProcessState.Complete, 0, 9, "b")
            },
            OnComplete = _ => Status(_ticket, ProcessState.Complete, 0, 9)
        };

        await ObserveAsync(Retrying(agent));

        agent.StatusRequests.Select(r => r.LastLogSequence).ShouldBe(new long[] { 0, 7, 7 });
    }

    [Fact]
    public async Task GetStatusOutageBeyondBudget_ThrowsTheOriginalException_AndSendsNoCancel()
    {
        var failure = new HalibutClientException("agent gone");
        var agent = new Agent { OnGetStatus = _ => throw failure };

        var thrown = await Should.ThrowAsync<HalibutClientException>(() => ObserveAsync(Retrying(agent, statusSeconds: 3), decision: () => false));

        thrown.ShouldBeSameAs(failure);
        agent.GetStatusCalls.ShouldBeGreaterThan(1, "it did retry");
        agent.CancelCalls.ShouldBe(0, "an infrastructure blip must leave the script running for the resume to re-attach");
    }

    [Fact]
    public async Task GetStatusPermanentFailure_IsNotRetried()
    {
        var agent = new Agent { OnGetStatus = _ => throw new ServiceInvocationHalibutClientException("agent service threw") };

        await Should.ThrowAsync<ServiceInvocationHalibutClientException>(() => ObserveAsync(Retrying(agent)));

        agent.GetStatusCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetStatusRetryBudget_IsCappedByTheScriptsRemainingTimeout()
    {
        var agent = new Agent { OnGetStatus = _ => throw new HalibutClientException("drop") };
        var client = Retrying(agent, scriptTimeout: TimeSpan.Zero);

        await Should.ThrowAsync<HalibutClientException>(() => client.GetStatusAsync(new ScriptStatusRequest(_ticket, 0)));

        agent.GetStatusCalls.ShouldBe(1, "retries must never extend the per-script timeout");
    }

    [Fact]
    public async Task StartScript_IsNeverRetried()
    {
        var agent = new Agent { OnStart = _ => throw new HalibutClientException("drop") };

        await Should.ThrowAsync<HalibutClientException>(() => Retrying(agent).StartScriptAsync(null));

        agent.StartCalls.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationWhileGetStatusBacksOff_StillCancelsTheScript_WhenDecisionSaysFinal()
    {
        using var cts = new CancellationTokenSource();
        var agent = new Agent { OnGetStatus = _ => throw new HalibutClientException("drop") };
        var cancelsDuringBackoff = new RpcRetryHandler(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), () => _now, (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var client = new RetryingScriptClient(agent, cancelsDuringBackoff, Plenty, cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => ObserveAsync(client, cts.Token, decision: () => true));

        agent.CancelCalls.ShouldBe(1, "a cancel that lands while GetStatus is backing off must still cancel the script exactly like one during the poll delay");
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task CancelRetry_OnlyHappensWhenTheDecisionSendsIt(bool decisionSaysCancel, int expectedCancelCalls)
    {
        using var cts = new CancellationTokenSource();
        var agent = new Agent
        {
            OnGetStatus = _ => Status(_ticket, ProcessState.Running, 0, 1),
            OnCancel = n => n == 1 ? throw new HalibutClientException("drop") : Status(_ticket, ProcessState.Complete, ScriptExitCodes.Canceled, 1)
        };
        cts.CancelAfter(20);

        await Should.ThrowAsync<OperationCanceledException>(() => ObserveAsync(Retrying(agent, ct: cts.Token), cts.Token, decision: () => decisionSaysCancel));

        agent.CancelCalls.ShouldBe(expectedCancelCalls);
    }

    [Fact]
    public async Task CancelThatNeverReachesTheAgent_GivesUpAfterBudget_WithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        var agent = new Agent { OnGetStatus = _ => Status(_ticket, ProcessState.Running, 0, 1), OnCancel = _ => throw new HalibutClientException("dead") };
        cts.CancelAfter(20);

        await Should.ThrowAsync<OperationCanceledException>(() => ObserveAsync(Retrying(agent, ct: cts.Token, statusSeconds: 3), cts.Token));

        agent.CancelCalls.ShouldBeGreaterThan(1);
        agent.CancelCalls.ShouldBeLessThan(10, "bounded by the budget, not unbounded");
    }

    // ── Complete (non-idempotent) ────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task CompleteResponseLost_RetryAnswersUnknown_ReturnsTheExitCodeAlreadyObserved(int realExitCode)
    {
        var agent = new Agent
        {
            OnGetStatus = n => n == 1 ? Status(_ticket, ProcessState.Running, 0, 1, "a") : Status(_ticket, ProcessState.Complete, realExitCode, 2, "b"),
            // 1st Complete executed on the agent (workspace deleted) but its response was lost; the retry finds nothing.
            OnComplete = n => n == 1 ? throw new HalibutClientException("response lost") : Status(_ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult, 0)
        };

        var result = await ObserveAsync(Retrying(agent));

        result.ExitCode.ShouldBe(realExitCode, "-1 here would turn a finished script into an unknown/failed one");
        result.Success.ShouldBe(realExitCode == 0);
        result.LogLines.ShouldBe(new[] { "a", "b" });
        agent.CompleteCalls.ShouldBe(2);
    }

    [Fact]
    public async Task CompleteFailedBeforeReachingAgent_RetryReturnsTheRealResult()
    {
        var agent = new Agent
        {
            OnGetStatus = n => n == 1 ? Status(_ticket, ProcessState.Running, 0, 1) : Status(_ticket, ProcessState.Complete, 0, 2),
            OnComplete = n => n == 1 ? throw new HalibutClientException("never delivered") : Status(_ticket, ProcessState.Complete, 0, 2, "tail")
        };

        var result = await ObserveAsync(Retrying(agent));

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain("tail");
    }

    [Fact]
    public async Task CompleteWithNoLostResponse_UnknownResultIsPassedThroughUntouched()
    {
        // No retry happened, so a -1 is the agent's genuine answer (e.g. an agent restart wiped the ticket) — must not be rewritten.
        var agent = new Agent
        {
            OnGetStatus = n => n == 1 ? Status(_ticket, ProcessState.Running, 0, 1) : Status(_ticket, ProcessState.Complete, 0, 2),
            OnComplete = _ => Status(_ticket, ProcessState.Complete, ScriptExitCodes.UnknownResult, 0)
        };

        var result = await ObserveAsync(Retrying(agent));

        result.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public async Task CompleteOutageBeyondBudget_ThrowsTheOriginalException()
    {
        var failure = new HalibutClientException("down");
        var agent = new Agent
        {
            OnGetStatus = n => n == 1 ? Status(_ticket, ProcessState.Running, 0, 1) : Status(_ticket, ProcessState.Complete, 0, 2),
            OnComplete = _ => throw failure
        };

        var thrown = await Should.ThrowAsync<HalibutClientException>(() => ObserveAsync(Retrying(agent, completeSeconds: 2)));

        thrown.ShouldBeSameAs(failure);
        agent.CompleteCalls.ShouldBeGreaterThan(1);
    }

    // ── Liveness stays authoritative ─────────────────────────────────────────

    [Fact]
    public async Task LivenessProbeDeclaringAgentUnreachable_StopsRetries_AndSurfacesAgentUnreachable()
    {
        var agent = new Agent { OnGetStatus = _ => throw new HalibutClientException("agent gone") };
        // Real time here: the probe loop runs on real timers; the 120s retry budget would otherwise hold the observer far past the probe's verdict.
        var client = new RetryingScriptClient(agent, new RpcRetryHandler(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30)), Plenty, CancellationToken.None);
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 }, new LivenessSettings { ProbeIntervalSeconds = 1, ProbeTimeoutSeconds = 1, FailureThreshold = 2 }, new DeadProbe());
        var endpoint = new ServiceEndPoint(new Uri("poll://fake/"), "AABBCCDD", HalibutTimeoutsAndLimits.RecommendedValues());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<AgentUnreachableException>(() => observer.ObserveAndCompleteAsync(_machine, client, _ticket, Plenty, CancellationToken.None, null, null, endpoint, null, () => false));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the liveness verdict must cut the retry wait short, not wait out the 120s budget");
    }

    private sealed class DeadProbe : IAgentLivenessProbe
    {
        public Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Agent : IAsyncScriptService
    {
        private int _start, _status, _complete, _cancel;

        public Func<int, ScriptStatusResponse> OnStart { get; init; }
        public Func<int, ScriptStatusResponse> OnGetStatus { get; init; }
        public Func<int, ScriptStatusResponse> OnComplete { get; init; }
        public Func<int, ScriptStatusResponse> OnCancel { get; init; }
        public List<ScriptStatusRequest> StatusRequests { get; } = new();

        public int StartCalls => _start;
        public int GetStatusCalls => _status;
        public int CompleteCalls => _complete;
        public int CancelCalls => _cancel;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => Run(OnStart, ref _start);

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            lock (StatusRequests) StatusRequests.Add(request);
            return Run(OnGetStatus, ref _status);
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => Run(OnComplete, ref _complete);

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) => Run(OnCancel, ref _cancel);

        private static Task<ScriptStatusResponse> Run(Func<int, ScriptStatusResponse> behaviour, ref int counter)
        {
            var n = Interlocked.Increment(ref counter);

            return Task.FromResult(behaviour?.Invoke(n) ?? throw new InvalidOperationException("unexpected call"));
        }
    }
}
