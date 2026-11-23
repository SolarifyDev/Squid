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
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Halibut;

/// <summary>
/// V1's retrying client answers a CompleteScript it cannot confirm with the exit code GetStatus already observed, so a finished
/// script is reported instead of pausing the deployment. The V2 client must behave the same (the rule is shared:
/// <see cref="ObservedCompletions{TObserved}"/>) — through the observer, the only caller that decides what the result is.
/// </summary>
public class RetryingScriptAgentClientCompletionTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromMinutes(30);

    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly ScriptTicket _ticket = new("t1");
    private readonly Machine _machine = new() { Name = "agent" };
    private readonly HalibutScriptObserver _observer = new(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });

    private RetryingScriptAgentClient Retrying(Scripted agent)
        => new(agent, new RpcRetryHandler(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), () => _now, (d, ct) => { ct.ThrowIfCancellationRequested(); _now += d; return Task.CompletedTask; }), Plenty, CancellationToken.None);

    private AgentScriptStatus Status(AgentScriptState state, int exit = 0) => new(_ticket, state, exit, new List<ProcessOutput>(), 0);

    private Task<ScriptExecutionResult> ObserveAsync(IScriptAgentClient client)
        => _observer.ObserveAgentScriptAsync(_machine, client, _ticket, Plenty, CancellationToken.None, null, null, null, null, null);

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public async Task CompleteCannotBeConfirmed_AfterGetStatusSawTheScriptFinish_TheObservedExitCodeIsTheResult(int exitCode, bool success)
    {
        var agent = new Scripted { OnGetStatus = () => Status(AgentScriptState.Completed, exitCode), OnComplete = () => throw new HalibutClientException("connection reset") };

        var result = await ObserveAsync(Retrying(agent));

        result.ExitCode.ShouldBe(exitCode);
        result.Success.ShouldBe(success);
        agent.CompleteCalls.ShouldBeGreaterThan(1, "the Complete was retried inside its budget before giving up");
    }

    [Fact]
    public async Task CompleteFails_ButNoCompletionWasEverObserved_StillRethrows_SoTheDeploymentPauses()
    {
        var agent = new Scripted { OnGetStatus = () => Status(AgentScriptState.Running), OnComplete = () => throw new HalibutClientException("connection reset") };

        // The status never reaches Completed, so the observer never calls Complete: drive the client directly.
        await Retrying(agent).GetStatusAsync(_ticket, 0);

        await Should.ThrowAsync<HalibutClientException>(() => Retrying(agent).CompleteAsync(_ticket, 0));
    }

    [Fact]
    public async Task CompleteFails_NonTransiently_IsNotSubstituted_EvenAfterAnObservedCompletion()
    {
        var agent = new Scripted { OnGetStatus = () => Status(AgentScriptState.Completed, 0), OnComplete = () => throw new InvalidOperationException("boom") };
        var client = Retrying(agent);
        await client.GetStatusAsync(_ticket, 0);

        await Should.ThrowAsync<InvalidOperationException>(() => client.CompleteAsync(_ticket, 0));
    }

    [Fact]
    public async Task ACompletionObservedForAnotherTicket_IsNotUsedForThisOne()
    {
        var other = new ScriptTicket("other");
        var agent = new Scripted { OnGetStatus = () => new AgentScriptStatus(other, AgentScriptState.Completed, 0, new List<ProcessOutput>(), 0), OnComplete = () => throw new HalibutClientException("connection reset") };
        var client = Retrying(agent);
        await client.GetStatusAsync(other, 0);

        await Should.ThrowAsync<HalibutClientException>(() => client.CompleteAsync(_ticket, 0));
    }

    [Fact]
    public async Task ACompleteThatSucceeds_IsReturnedUnchanged_NotReplacedByTheObservedStatus()
    {
        var final = Status(AgentScriptState.Completed, 7);
        var agent = new Scripted { OnGetStatus = () => Status(AgentScriptState.Completed, 7), OnComplete = () => new AgentScriptCompletion(AgentCompleteOutcome.Completed, final) };
        var client = Retrying(agent);
        await client.GetStatusAsync(_ticket, 0);

        var completion = await client.CompleteAsync(_ticket, 0);

        completion.Outcome.ShouldBe(AgentCompleteOutcome.Completed);
        completion.Final.ShouldBeSameAs(final);
    }

    private sealed class Scripted : IScriptAgentClient
    {
        public Func<AgentScriptStatus> OnGetStatus { get; set; }
        public Func<AgentScriptCompletion> OnComplete { get; set; }
        public int CompleteCalls { get; private set; }

        public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence) => Task.FromResult(OnGetStatus());

        public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence) => throw new NotSupportedException();

        public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
        {
            CompleteCalls++;

            return Task.FromResult(OnComplete());
        }
    }
}
