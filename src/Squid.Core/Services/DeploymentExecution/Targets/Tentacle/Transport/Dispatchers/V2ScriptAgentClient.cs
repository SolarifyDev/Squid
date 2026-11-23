using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;

/// <summary>
/// Adapts the raw V2 client to <see cref="IScriptAgentClient"/>. Every V2 state and outcome keeps its own
/// neutral value — nothing is folded into an exit code. A value the contract forbids (the wire
/// <c>Unspecified</c> default, or a completed outcome without a final status) is a protocol violation, not
/// something to interpret.
/// </summary>
internal sealed class V2ScriptAgentClient : IScriptAgentClient
{
    private readonly IAsyncScriptServiceV2 _inner;

    public V2ScriptAgentClient(IAsyncScriptServiceV2 inner)
    {
        _inner = inner;
    }

    public async Task<AgentScriptStatus> StartAsync(StartScriptCommandV2 command)
        => ToNeutral(await _inner.StartScriptAsync(command).ConfigureAwait(false));

    public async Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
        => ToNeutral(await _inner.GetStatusAsync(new ScriptStatusRequestV2(ticket, lastLogSequence)).ConfigureAwait(false));

    public async Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence)
        => ToNeutral(await _inner.CancelScriptAsync(new CancelScriptCommandV2(ticket, lastLogSequence)).ConfigureAwait(false));

    public async Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
        => ToNeutral(await _inner.CompleteScriptAsync(new CompleteScriptCommandV2(ticket, lastLogSequence)).ConfigureAwait(false));

    internal static AgentScriptStatus ToNeutral(ScriptStatusResponseV2 response)
        => new(response.Ticket, ToNeutral(response.State), response.ExitCode, response.Logs ?? new List<ProcessOutput>(), response.NextLogSequence);

    internal static AgentScriptCompletion ToNeutral(CompleteScriptResponseV2 response)
    {
        var outcome = ToNeutral(response.Outcome);
        var final = response.FinalStatus == null ? null : ToNeutral(response.FinalStatus);

        if (final == null && outcome is AgentCompleteOutcome.Completed or AgentCompleteOutcome.NotYetComplete)
            throw new AgentProtocolViolationException($"CompleteScript answered {response.Outcome} without a final status");

        return new AgentScriptCompletion(outcome, final);
    }

    private static AgentScriptState ToNeutral(ScriptStateV2 state) => state switch
    {
        ScriptStateV2.Pending => AgentScriptState.Pending,
        ScriptStateV2.Running => AgentScriptState.Running,
        ScriptStateV2.Completed => AgentScriptState.Completed,
        ScriptStateV2.UnknownTicket => AgentScriptState.UnknownTicket,
        ScriptStateV2.ResultLost => AgentScriptState.ResultLost,
        _ => throw new AgentProtocolViolationException($"script state {state} is not a defined state")
    };

    private static AgentCompleteOutcome ToNeutral(CompleteScriptOutcomeV2 outcome) => outcome switch
    {
        CompleteScriptOutcomeV2.Completed => AgentCompleteOutcome.Completed,
        CompleteScriptOutcomeV2.AlreadyCompleted => AgentCompleteOutcome.AlreadyCompleted,
        CompleteScriptOutcomeV2.UnknownTicket => AgentCompleteOutcome.UnknownTicket,
        CompleteScriptOutcomeV2.NotYetComplete => AgentCompleteOutcome.NotYetComplete,
        _ => throw new AgentProtocolViolationException($"complete outcome {outcome} is not a defined outcome")
    };
}
