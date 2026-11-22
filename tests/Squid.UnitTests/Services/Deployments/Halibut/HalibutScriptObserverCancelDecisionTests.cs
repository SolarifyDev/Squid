using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
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
/// Pins WHEN <see cref="HalibutScriptObserver"/> sends <c>CancelScript</c> to the agent. A paused
/// deployment (resumable timeout, host shutdown, unreachable agent) deliberately preserves the
/// in-flight script pointer so a resume re-attaches to the still-running script — so the observer must
/// not kill that script. The decision is supplied by the party that knows why the run was interrupted
/// (<see cref="RemoteScriptCancelDecision"/>); absent, the historical "always cancel" holds for the
/// upgrade strategies and every other caller.
/// </summary>
public class HalibutScriptObserverCancelDecisionTests
{
    public enum Interruption
    {
        CancelledDuringPollDelay,
        CancelledBeforePoll,
        AgentUnreachable,
        PerScriptTimeout
    }

    public enum Decision
    {
        NotSupplied,
        SaysCancel,
        SaysLeaveRunning
    }

    [Theory]
    // Cancellation token fired: cancel iff no decision was supplied or it says the outcome is final.
    [InlineData(Interruption.CancelledDuringPollDelay, Decision.NotSupplied, 1)]
    [InlineData(Interruption.CancelledDuringPollDelay, Decision.SaysCancel, 1)]
    [InlineData(Interruption.CancelledDuringPollDelay, Decision.SaysLeaveRunning, 0)]
    [InlineData(Interruption.CancelledBeforePoll, Decision.NotSupplied, 1)]
    [InlineData(Interruption.CancelledBeforePoll, Decision.SaysCancel, 1)]
    [InlineData(Interruption.CancelledBeforePoll, Decision.SaysLeaveRunning, 0)]
    // Unreachable agent: a supplied decision means the task pauses, so NEVER cancel (even if it says
    // cancel — that answer is only meaningful for a token interruption). Legacy callers still cancel.
    [InlineData(Interruption.AgentUnreachable, Decision.NotSupplied, 1)]
    [InlineData(Interruption.AgentUnreachable, Decision.SaysCancel, 0)]
    [InlineData(Interruption.AgentUnreachable, Decision.SaysLeaveRunning, 0)]
    // Per-script timeout is a definitive failure of the script itself: always cancel.
    [InlineData(Interruption.PerScriptTimeout, Decision.NotSupplied, 1)]
    [InlineData(Interruption.PerScriptTimeout, Decision.SaysCancel, 1)]
    [InlineData(Interruption.PerScriptTimeout, Decision.SaysLeaveRunning, 1)]
    public async Task Interruption_SendsCancelScriptOnlyWhenOutcomeIsFinal(Interruption interruption, Decision decision, int expectedCancelCalls)
    {
        var agent = new BlockingAgent();
        using var cts = new CancellationTokenSource();
        var evaluations = 0;
        RemoteScriptCancelDecision supplied = decision switch
        {
            Decision.SaysCancel => () => { evaluations++; return true; },
            Decision.SaysLeaveRunning => () => { evaluations++; return false; },
            _ => null
        };

        await RunInterruptedAsync(interruption, agent, cts, supplied).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(expectedCancelCalls,
            customMessage: $"{interruption} with decision {decision}: CancelScript must be sent exactly when the task is heading terminal " +
                           "(or no decision was supplied). Sending it for a pause kills the script a resume is meant to re-attach to; " +
                           "withholding it for a terminal outcome leaks a running script.");

        if (interruption == Interruption.AgentUnreachable || interruption == Interruption.PerScriptTimeout)
            evaluations.ShouldBe(0, "the decision is only consulted for a cancellation-token interruption");
    }

    [Fact]
    public async Task DecisionIsEvaluatedAtInterruptionTime_NotAtCallStart()
    {
        // The runner's decision reads source flags that flip DURING the run (user cancel / timeout).
        // A value captured at call start would always be "false" here and never cancel.
        var agent = new BlockingAgent();
        using var cts = new CancellationTokenSource();
        var terminal = false;

        agent.OnGetStatus = () => { terminal = true; cts.Cancel(); };

        await Should.ThrowAsync<OperationCanceledException>(() => NewObserver().ObserveAndCompleteAsync(
            new Machine { Name = "m" }, agent, new ScriptTicket("t"), TimeSpan.FromMinutes(5), cts.Token,
            null, null, null, null, () => terminal)).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(1);
    }

    [Fact]
    public async Task NineArgumentOverload_StillCancelsOnCancellation()
    {
        // The overload every pre-existing caller (upgrade strategies, StubSquidServer fixtures) binds to.
        var agent = new BlockingAgent();
        using var cts = new CancellationTokenSource();
        agent.OnGetStatus = () => cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => NewObserver().ObserveAndCompleteAsync(
            new Machine { Name = "m" }, agent, new ScriptTicket("t"), TimeSpan.FromMinutes(5), cts.Token)).ConfigureAwait(false);

        agent.CancelCalls.ShouldBe(1);
    }

    private static async Task RunInterruptedAsync(Interruption interruption, BlockingAgent agent, CancellationTokenSource cts, RemoteScriptCancelDecision decision)
    {
        var machine = new Machine { Name = "m" };
        var ticket = new ScriptTicket("t");

        switch (interruption)
        {
            case Interruption.CancelledDuringPollDelay:
                agent.OnGetStatus = () => cts.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(() => Observe(NewObserver(), machine, agent, ticket, TimeSpan.FromMinutes(5), cts.Token, null, decision)).ConfigureAwait(false);
                break;

            case Interruption.CancelledBeforePoll:
                cts.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(() => Observe(NewObserver(), machine, agent, ticket, TimeSpan.FromMinutes(5), cts.Token, null, decision)).ConfigureAwait(false);
                break;

            case Interruption.AgentUnreachable:
                var observer = NewObserver(new DeadProbe());
                var endpoint = new ServiceEndPoint(new Uri("poll://fake/"), "AABBCCDD", global::Halibut.Diagnostics.HalibutTimeoutsAndLimits.RecommendedValues());
                await Should.ThrowAsync<AgentUnreachableException>(() => Observe(observer, machine, agent, ticket, TimeSpan.FromMinutes(5), cts.Token, endpoint, decision)).ConfigureAwait(false);
                break;

            case Interruption.PerScriptTimeout:
                var result = await Observe(NewObserver(), machine, agent, ticket, TimeSpan.Zero, cts.Token, null, decision).ConfigureAwait(false);
                result.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
                break;
        }
    }

    private static Task<ScriptExecutionResult> Observe(HalibutScriptObserver observer, Machine machine, IAsyncScriptService agent, ScriptTicket ticket, TimeSpan timeout, CancellationToken ct, ServiceEndPoint endpoint, RemoteScriptCancelDecision decision)
        => decision == null
            ? observer.ObserveAndCompleteAsync(machine, agent, ticket, timeout, ct, masker: null, initialStartResponse: null, endpoint: endpoint)
            : observer.ObserveAndCompleteAsync(machine, agent, ticket, timeout, ct, null, null, endpoint, null, decision);

    private static HalibutScriptObserver NewObserver(IAgentLivenessProbe probe = null)
        => new(new ObserverSettings { InitialPollIntervalMs = 20, MaxPollIntervalMs = 50 }, new LivenessSettings { ProbeIntervalSeconds = 1, ProbeTimeoutSeconds = 1, FailureThreshold = 1 }, probe);

    private sealed class DeadProbe : IAgentLivenessProbe
    {
        public Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class BlockingAgent : IAsyncScriptService
    {
        public int CancelCalls;
        public Action OnGetStatus { get; set; }

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            OnGetStatus?.Invoke();
            return Task.FromResult(new ScriptStatusResponse(request.Ticket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) => throw new NotSupportedException();

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        {
            Interlocked.Increment(ref CancelCalls);
            return Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        }
    }
}
