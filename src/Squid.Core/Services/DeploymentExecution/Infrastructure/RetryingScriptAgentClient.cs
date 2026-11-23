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
/// <para>Unlike V1 it needs no "remember the observed completion" repair: a retried Complete after a lost
/// response answers an explicit <see cref="AgentCompleteOutcome.AlreadyCompleted"/> instead of a fabricated
/// -1, and the observer reads the real exit code from the status it already saw.</para>
/// </summary>
internal sealed class RetryingScriptAgentClient : IScriptAgentClient, IStopRetryingAware
{
    private readonly IScriptAgentClient _inner;
    private readonly RpcRetryHandler _retry;
    private readonly DateTimeOffset _scriptDeadline;
    private readonly CancellationToken _ct;
    private CancellationToken _stopRetrying;

    public RetryingScriptAgentClient(IScriptAgentClient inner, RpcRetryHandler retry, TimeSpan scriptTimeout, CancellationToken ct)
    {
        _inner = inner;
        _retry = retry;
        _scriptDeadline = DateTimeOffset.UtcNow + scriptTimeout;
        _ct = ct;
    }

    public void BindStopRetrying(CancellationToken stopRetrying) => _stopRetrying = stopRetrying;

    public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
    {
        var budget = Min(_retry.StatusBudget, _scriptDeadline - DateTimeOffset.UtcNow);

        return _retry.ExecuteAsync(() => _inner.GetStatusAsync(ticket, lastLogSequence), "GetStatus", budget, _ct, _stopRetrying);
    }

    public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence)
        => _retry.ExecuteAsync(() => _inner.CancelAsync(ticket, lastLogSequence), "CancelScript", _retry.CancelBudget, CancellationToken.None);

    public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
        => _retry.ExecuteAsync(() => _inner.CompleteAsync(ticket, lastLogSequence), "CompleteScript", _retry.CompleteBudget, _ct, _stopRetrying);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
