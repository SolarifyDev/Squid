using Squid.Core.Halibut.Resilience;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

/// <summary>
/// The protocol-neutral counterpart of <see cref="RetryingScriptClient"/> for protocols whose calls are
/// idempotent per ticket (V2): GetStatus / Cancel / Complete survive a dropped connection inside the same
/// <see cref="RpcRetryHandler"/> budgets the V1 client uses — status capped by the script's remaining
/// timeout, cancel with no caller token and its own shorter budget, complete with its best-effort budget —
/// and every exhausted budget rethrows the original exception.
///
/// <para>Unlike V1 it needs no reconcile of a retried Complete: a retried Complete after a lost response answers an
/// explicit <see cref="AgentCompleteOutcome.AlreadyCompleted"/> instead of a fabricated -1, and the observer reads the real
/// exit code from the status it already saw. It does share V1's rule for a Complete that cannot be confirmed at all
/// (<see cref="ObservedCompletions{TObserved}"/>): once GetStatus reported the script completed, a failing Complete is answered
/// <see cref="AgentCompleteOutcome.AlreadyCompleted"/> (the observer keeps the exit code it already saw) rather than pausing.</para>
/// </summary>
internal sealed class RetryingScriptAgentClient : IScriptAgentClient, IStopRetryingAware
{
    private readonly IScriptAgentClient _inner;
    private readonly RpcRetryHandler _retry;
    private readonly DateTimeOffset _scriptDeadline;
    private readonly CancellationToken _ct;
    private CancellationToken _stopRetrying;
    private readonly ObservedCompletions<AgentScriptStatus> _observedComplete = new();

    public RetryingScriptAgentClient(IScriptAgentClient inner, RpcRetryHandler retry, TimeSpan scriptTimeout, CancellationToken ct)
    {
        _inner = inner;
        _retry = retry;
        _scriptDeadline = DateTimeOffset.UtcNow + scriptTimeout;
        _ct = ct;
    }

    public void BindStopRetrying(CancellationToken stopRetrying) => _stopRetrying = stopRetrying;

    public async Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
    {
        var budget = Min(_retry.StatusBudget, _scriptDeadline - DateTimeOffset.UtcNow);

        var status = await _retry.ExecuteAsync(() => _inner.GetStatusAsync(ticket, lastLogSequence), "GetStatus", budget, _ct, _stopRetrying).ConfigureAwait(false);

        if (status.State == AgentScriptState.Completed) _observedComplete.Remember(ticket.TaskId, status);

        return status;
    }

    public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence)
        => _retry.ExecuteAsync(() => _inner.CancelAsync(ticket, lastLogSequence), "CancelScript", _retry.CancelBudget, CancellationToken.None, boundEachAttempt: true);

    public async Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
    {
        try
        {
            return await _retry.ExecuteAsync(() => _inner.CompleteAsync(ticket, lastLogSequence), "CompleteScript", _retry.CompleteBudget, _ct, _stopRetrying).ConfigureAwait(false);
        }
        catch (Exception ex) when (_observedComplete.CanSubstituteFor(ex, ticket.TaskId))
        {
            Log.Warning(ex, "[Deploy] CompleteScript for ticket {Ticket} could not be confirmed; using the exit code {ExitCode} already observed via GetStatus", ticket.TaskId, _observedComplete.Get(ticket.TaskId).ExitCode);

            return new AgentScriptCompletion(AgentCompleteOutcome.AlreadyCompleted, null);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
