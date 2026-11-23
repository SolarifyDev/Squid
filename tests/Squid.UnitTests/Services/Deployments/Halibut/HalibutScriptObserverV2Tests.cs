using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Halibut;

/// <summary>
/// The observer's handling of V2's explicit outcomes, against a scripted protocol-neutral client: a script
/// that really exits -1 is a completed result; an unknown ticket or lost result fails the action with an
/// operator-readable message and never asks the agent to complete (nothing to complete) or re-runs anything;
/// a Complete that answers "not yet complete" keeps observing; "already completed" / "unknown" after a lost
/// Complete response reuse the exit code GetStatus already observed.
/// </summary>
public class HalibutScriptObserverV2Tests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromMinutes(30);

    private readonly ScriptTicket _ticket = new("v2-ticket");
    private readonly Machine _machine = new() { Name = "agent-v2" };
    private readonly HalibutScriptObserver _observer = new(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });

    private AgentScriptStatus Status(AgentScriptState state, int exit = 0, long next = 0, params string[] lines)
        => new(_ticket, state, exit, lines.Select(l => new ProcessOutput(ProcessOutputSource.StdOut, l, DateTimeOffset.UtcNow)).ToList(), next);

    private AgentScriptCompletion Complete(AgentCompleteOutcome outcome, AgentScriptStatus final = null) => new(outcome, final);

    private Task<ScriptExecutionResult> ObserveAsync(ScriptedClient client, AgentScriptStatus initial = null)
        => _observer.ObserveAgentScriptAsync(_machine, client, _ticket, Plenty, CancellationToken.None, null, initial, null, null, null);

    [Fact]
    public async Task ScriptThatReallyExitedMinusOne_IsACompletedResult_NotTreatedAsLost()
    {
        var client = new ScriptedClient { OnStatus = _ => Status(AgentScriptState.Completed, ScriptExitCodes.UnknownResult), OnComplete = _ => Complete(AgentCompleteOutcome.Completed, Status(AgentScriptState.Completed, ScriptExitCodes.UnknownResult)) };

        var result = await ObserveAsync(client);

        result.ExitCode.ShouldBe(-1);
        result.Success.ShouldBeFalse();
        result.StderrLines.ShouldBeEmpty("exit -1 over V2 is just a failed script, not the 'unknown result' condition");
        client.CompleteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task PendingThenRunningThenCompleted_UsesTheExitCodeAndLogsFromComplete()
    {
        var client = new ScriptedClient
        {
            OnStatus = n => n switch
            {
                1 => Status(AgentScriptState.Pending),
                2 => Status(AgentScriptState.Running, 0, 1, "a"),
                _ => Status(AgentScriptState.Completed, 7, 2, "b")
            },
            OnComplete = _ => Complete(AgentCompleteOutcome.Completed, Status(AgentScriptState.Completed, 7, 3, "c"))
        };

        var result = await ObserveAsync(client);

        result.ExitCode.ShouldBe(7);
        result.LogLines.ShouldBe(new[] { "a", "b", "c" });
    }

    [Theory]
    [InlineData(AgentScriptState.ResultLost, "lost the result")]
    [InlineData(AgentScriptState.UnknownTicket, "no longer knows script")]
    public async Task LostOrUnknownWhileObserving_FailsTheAction_WithAnOperatorReadableMessage_AndNeverCompletesOrCancels(AgentScriptState state, string expectedText)
    {
        var client = new ScriptedClient { OnStatus = n => n == 1 ? Status(AgentScriptState.Running, 0, 1, "partial output") : Status(state) };

        var result = await ObserveAsync(client);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
        result.LogLines.ShouldBe(new[] { "partial output", result.StderrLines.Single() });
        result.StderrLines.Single().ShouldContain(expectedText);
        result.StderrLines.Single().ShouldContain("did NOT run it again");
        result.BuildErrorSummary().ShouldContain("Check the target");
        client.CompleteCalls.ShouldBe(0);
        client.CancelCalls.ShouldBe(0);
    }

    [Fact]
    public async Task LostResultAsTheInitialStatus_FailsWithoutPollingAtAll()
    {
        var client = new ScriptedClient { OnStatus = _ => throw new InvalidOperationException("must not poll") };

        var result = await ObserveAsync(client, Status(AgentScriptState.ResultLost));

        result.Success.ShouldBeFalse();
        client.StatusCalls.ShouldBe(0);
    }

    [Fact]
    public async Task LostResult_StreamsTheExplanation_WhenAnOutputSinkIsAttached()
    {
        var streamed = new List<ScriptOutputLine>();
        var client = new ScriptedClient { OnStatus = _ => Status(AgentScriptState.ResultLost) };

        var result = await _observer.ObserveAgentScriptAsync(_machine, client, _ticket, Plenty, CancellationToken.None, null, null, null, (lines, _) => { streamed.AddRange(lines); return Task.CompletedTask; }, null);

        streamed.ShouldHaveSingleItem().IsStdErr.ShouldBeTrue();
        streamed[0].Text.ShouldBe(result.StderrLines.Single());
        result.OutputStreamed.ShouldBeTrue("the lines already reached the task log live, so they must not be persisted a second time");
    }

    [Fact]
    public async Task CompleteNotYetComplete_KeepsObserving_ThenUsesTheLaterResult()
    {
        var completeCalls = 0;
        var client = new ScriptedClient
        {
            OnStatus = n => n <= 1 ? Status(AgentScriptState.Completed, 0, 1) : n == 2 ? Status(AgentScriptState.Running, 0, 1) : Status(AgentScriptState.Completed, 4, 2),
            OnComplete = _ => ++completeCalls == 1
                ? Complete(AgentCompleteOutcome.NotYetComplete, Status(AgentScriptState.Running, 0, 1, "still going"))
                : Complete(AgentCompleteOutcome.Completed, Status(AgentScriptState.Completed, 4, 2, "done"))
        };

        var result = await ObserveAsync(client);

        completeCalls.ShouldBe(2, "a not-yet-complete answer is not a result: observation resumes and Complete is asked again");
        result.ExitCode.ShouldBe(4);
        result.LogLines.ShouldBe(new[] { "still going", "done" });
    }

    [Fact]
    public async Task CompleteNotYetComplete_WithATerminalFinalStatus_StillKeepsObserving_InsteadOfSpinningOnIt()
    {
        var completeCalls = 0;
        var client = new ScriptedClient
        {
            OnStatus = _ => Status(AgentScriptState.Completed, 5, 1),
            OnComplete = _ => ++completeCalls == 1 ? Complete(AgentCompleteOutcome.NotYetComplete, Status(AgentScriptState.Completed, 5, 1)) : Complete(AgentCompleteOutcome.Completed, Status(AgentScriptState.Completed, 5, 1))
        };

        var result = await ObserveAsync(client);

        result.ExitCode.ShouldBe(5);
        completeCalls.ShouldBe(2);
    }

    [Theory]
    [InlineData(AgentCompleteOutcome.AlreadyCompleted)]
    [InlineData(AgentCompleteOutcome.UnknownTicket)]
    public async Task CompleteAnsweringAlreadyCompletedOrUnknown_AfterALostResponse_UsesTheExitCodeAlreadyObserved(AgentCompleteOutcome outcome)
    {
        var client = new ScriptedClient { OnStatus = _ => Status(AgentScriptState.Completed, 9, 1, "ran"), OnComplete = _ => Complete(outcome) };

        var result = await ObserveAsync(client);

        result.ExitCode.ShouldBe(9, "the real exit code came from GetStatus; only the agent-side release was unconfirmed");
        result.LogLines.ShouldBe(new[] { "ran" });
    }

    private sealed class ScriptedClient : IScriptAgentClient
    {
        public Func<int, AgentScriptStatus> OnStatus { get; set; } = _ => throw new InvalidOperationException("no status scripted");
        public Func<int, AgentScriptCompletion> OnComplete { get; set; } = _ => throw new InvalidOperationException("no complete scripted");
        public int StatusCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence) => Task.FromResult(OnStatus(++StatusCalls));

        public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence)
        {
            CancelCalls++;

            return Task.FromResult(new AgentScriptStatus(ticket, AgentScriptState.Completed, ScriptExitCodes.Canceled, new List<ProcessOutput>(), 0));
        }

        public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence) => Task.FromResult(OnComplete(++CompleteCalls));
    }
}
