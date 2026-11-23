using System.Collections.Concurrent;
using Squid.Core.Halibut.Resilience;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

/// <summary>
/// A script client whose retries can be stopped by the observer (e.g. once the liveness probe has
/// declared the agent unreachable, further retries are pointless and would only delay the pause).
/// </summary>
public interface IStopRetryingAware
{
    void BindStopRetrying(CancellationToken stopRetrying);
}

/// <summary>
/// Decorates the per-dispatch agent client so GetStatus / Cancel / Complete survive a dropped
/// connection inside a time budget (<see cref="RpcRetryHandler"/>). Applied ONLY by the deployment
/// execution strategy: the upgrade strategies keep the raw client because a mid-script disconnect is
/// the expected signal there (the upgrade restarts the agent) and a retry would observe the restarted
/// agent's "unknown ticket" answer instead.
///
/// <para>StartScript is never retried.</para>
///
/// <para>GetStatus is idempotent: the observer advances its log cursor only after a response arrives,
/// so a retry re-requests the same range. Its retries are capped by the script's remaining timeout so
/// retrying cannot extend the per-script timeout.</para>
///
/// <para>Cancel is idempotent (an unknown ticket answers Canceled). It is only reached when the observer
/// decided to send it (<see cref="Script.RemoteScriptCancelDecision"/>); it is retried with no caller
/// token because it normally runs after cancellation, bounded by the budget.</para>
///
/// <para>Complete is NOT idempotent on either agent backend: the first successful call deletes the
/// workspace and state, so a retry after a lost RESPONSE answers Complete + UnknownResult (-1). When
/// that happens and an earlier GetStatus already saw the real Complete result for this ticket, that
/// result is returned instead of -1, so a finished script is never reported as failed.</para>
/// </summary>
public sealed class RetryingScriptClient : IAsyncScriptService, IStopRetryingAware
{
    private readonly IAsyncScriptService _inner;
    private readonly RpcRetryHandler _retry;
    private readonly DateTimeOffset _scriptDeadline;
    private readonly CancellationToken _ct;
    private CancellationToken _stopRetrying;
    private readonly ConcurrentDictionary<string, ScriptStatusResponse> _observedComplete = new();

    public RetryingScriptClient(IAsyncScriptService inner, RpcRetryHandler retry, TimeSpan scriptTimeout, CancellationToken ct)
    {
        _inner = inner;
        _retry = retry;
        _scriptDeadline = DateTimeOffset.UtcNow + scriptTimeout;
        _ct = ct;
    }

    public void BindStopRetrying(CancellationToken stopRetrying) => _stopRetrying = stopRetrying;

    public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) => _inner.StartScriptAsync(command);

    public async Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
    {
        var budget = Min(_retry.StatusBudget, _scriptDeadline - DateTimeOffset.UtcNow);

        var response = await _retry.ExecuteAsync(() => _inner.GetStatusAsync(request), "GetStatus", budget, _ct, _stopRetrying).ConfigureAwait(false);

        RememberIfRealCompletion(request.Ticket, response);

        return response;
    }

    public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
        => _retry.ExecuteAsync(() => _inner.CancelScriptAsync(command), "CancelScript", _retry.CancelBudget, CancellationToken.None, boundEachAttempt: true);

    public async Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
    {
        var attempts = 0;

        try
        {
            var response = await _retry.ExecuteAsync(() => { attempts++; return _inner.CompleteScriptAsync(command); }, "CompleteScript", _retry.CompleteBudget, _ct, _stopRetrying).ConfigureAwait(false);

            return attempts > 1 ? ReconcileRetriedComplete(command, response) : response;
        }
        catch (Exception ex) when (TransientFailureClassifier.IsTransient(ex) && _observedComplete.ContainsKey(command.Ticket.TaskId))
        {
            return UseObservedCompletion(command, ex);
        }
    }

    /// <summary>
    /// Complete could not be confirmed, but GetStatus already reported the script finished with its real
    /// exit code: the outcome is known. Rethrowing would pause the deployment, and a resume would find the
    /// ticket gone (-1) and re-dispatch a script that already ran. Only the agent-side cleanup is unconfirmed.
    /// </summary>
    private ScriptStatusResponse UseObservedCompletion(CompleteScriptCommand command, Exception ex)
    {
        var observed = _observedComplete[command.Ticket.TaskId];

        Log.Warning(ex, "[Deploy] CompleteScript for ticket {Ticket} could not be confirmed; using the exit code {ExitCode} already observed via GetStatus", command.Ticket.TaskId, observed.ExitCode);

        return BuildFromObserved(command, observed);
    }

    private static ScriptStatusResponse BuildFromObserved(CompleteScriptCommand command, ScriptStatusResponse observed)
        => new(command.Ticket, ProcessState.Complete, observed.ExitCode, new List<ProcessOutput>(), command.LastLogSequence);

    private ScriptStatusResponse ReconcileRetriedComplete(CompleteScriptCommand command, ScriptStatusResponse response)
    {
        if (!IsUnknownResult(response)) return response;
        if (!_observedComplete.TryGetValue(command.Ticket.TaskId, out var observed)) return response;

        Log.Warning("[Deploy] CompleteScript retry for ticket {Ticket} answered unknown-result after a lost response; using the exit code {ExitCode} already observed via GetStatus", command.Ticket.TaskId, observed.ExitCode);

        return BuildFromObserved(command, observed);
    }

    private void RememberIfRealCompletion(ScriptTicket ticket, ScriptStatusResponse response)
    {
        if (response.State == ProcessState.Complete && !IsUnknownResult(response))
            _observedComplete[ticket.TaskId] = response;
    }

    private static bool IsUnknownResult(ScriptStatusResponse response)
        => response.State == ProcessState.Complete && response.ExitCode == ScriptExitCodes.UnknownResult;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
