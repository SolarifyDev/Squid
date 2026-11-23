using Halibut;
using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// The single per-RPC retry loop for agent calls that outlive a dropped connection (script GetStatus /
/// Cancel / Complete). A call is retried only while the failure is TRANSIENT per
/// <see cref="TransientFailureClassifier"/> (never a second classification), only inside a total time
/// budget (<see cref="RpcRetrySettings"/>), with exponential backoff, and never once the caller's token fires. When the budget runs out
/// (or the failure is not transient) the exception the agent call actually threw is rethrown unchanged,
/// so every downstream classifier (the runner's transient-pause path, the circuit breaker) sees exactly
/// what it saw before retries existed.
///
/// <para>The budget normally bounds when the LAST ATTEMPT MAY START, not how long it runs: one attempt can itself wait the
/// polling-queue timeout (120 s by default) or Halibut's listening-connect retry window. A caller that needs a hard latency bound
/// (CancelScript) passes <c>boundEachAttempt</c>: an attempt still running when the budget is spent is abandoned and a transient
/// <see cref="HalibutClientException"/> is thrown. The Halibut proxy interfaces take no cancellation token, so the abandoned
/// request cannot be withdrawn; it may still be delivered later, which is harmless for the idempotent cancel.</para>
/// </summary>
public sealed class RpcRetryHandler
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public RpcRetryHandler(RpcRetrySettings settings) : this(Resolve(settings?.BudgetSeconds, RpcRetrySettings.DefaultBudgetSeconds), Resolve(settings?.CompleteBudgetSeconds, RpcRetrySettings.DefaultCompleteBudgetSeconds), Resolve(settings?.CancelBudgetSeconds, RpcRetrySettings.DefaultCancelBudgetSeconds), null, null) { }

    internal RpcRetryHandler(TimeSpan statusBudget, TimeSpan completeBudget, TimeSpan cancelBudget) : this(statusBudget, completeBudget, cancelBudget, null, null) { }

    internal RpcRetryHandler(TimeSpan statusBudget, TimeSpan completeBudget, TimeSpan cancelBudget, Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        StatusBudget = statusBudget;
        CompleteBudget = completeBudget;
        CancelBudget = cancelBudget;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>Budget for GetStatus.</summary>
    public TimeSpan StatusBudget { get; }

    /// <summary>Shorter, best-effort budget for CompleteScript.</summary>
    public TimeSpan CompleteBudget { get; }

    /// <summary>
    /// Hard total-time bound for CancelScript (the call sites pass <c>boundEachAttempt</c>): a user cancel or script-timeout cancel to a
    /// dead agent keeps the task in Cancelling while it retries, so cancel latency must stay bounded.
    /// </summary>
    public TimeSpan CancelBudget { get; }

    /// <summary>Seconds from configuration; zero, negative or unset is invalid and falls back to the default.</summary>
    internal static TimeSpan Resolve(int? configuredSeconds, int defaultSeconds)
        => TimeSpan.FromSeconds(configuredSeconds is > 0 ? configuredSeconds.Value : defaultSeconds);

    /// <param name="cancellation">Caller cancellation: aborts the backoff wait with <see cref="OperationCanceledException"/>.</param>
    /// <param name="stopRetrying">Stops further retries and rethrows the last real failure (e.g. the liveness probe already declared the agent gone).</param>
    /// <param name="boundEachAttempt">Makes the budget a hard bound on total time: an attempt still running at the deadline is abandoned (see the class remarks).</param>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> call, string rpcName, TimeSpan budget, CancellationToken cancellation, CancellationToken stopRetrying = default, bool boundEachAttempt = false)
    {
        var started = _utcNow();
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                return await RunAttemptAsync(call, rpcName, boundEachAttempt, budget - (_utcNow() - started)).ConfigureAwait(false);
            }
            catch (AttemptAbandonedException)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellation, stopRetrying))
            {
                var delay = NextDelay(attempt, budget - (_utcNow() - started), boundEachAttempt);

                if (delay == null) throw;

                Log.Warning(ex, "[Deploy] Agent RPC {Rpc} failed transiently (attempt {Attempt}); retrying in {DelayMs}ms", rpcName, attempt, (int)delay.Value.TotalMilliseconds);

                if (!await WaitAsync(delay.Value, cancellation, stopRetrying).ConfigureAwait(false)) throw;
            }
        }
    }

    private async Task<T> RunAttemptAsync<T>(Func<Task<T>> call, string rpcName, bool boundEachAttempt, TimeSpan remaining)
    {
        // Budget already spent before the first attempt (e.g. the script deadline passed): run it once, unbounded, as before.
        if (!boundEachAttempt || remaining <= TimeSpan.Zero) return await call().ConfigureAwait(false);

        var attempt = call();

        using var deadline = new CancellationTokenSource();
        var expired = Task.Delay(remaining, deadline.Token);

        if (await Task.WhenAny(attempt, expired).ConfigureAwait(false) == attempt)
        {
            deadline.Cancel();

            return await attempt.ConfigureAwait(false);
        }

        _ = attempt.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        Log.Warning("[Deploy] Agent RPC {Rpc} did not finish within its {BudgetSeconds}s budget; abandoning the attempt", rpcName, (int)remaining.TotalSeconds);

        throw new AttemptAbandonedException($"Agent RPC {rpcName} did not complete within its time budget; the attempt was abandoned");
    }

    private sealed class AttemptAbandonedException(string message) : HalibutClientException(message);

    private static bool IsRetryable(Exception ex, CancellationToken cancellation, CancellationToken stopRetrying)
        => !cancellation.IsCancellationRequested && !stopRetrying.IsCancellationRequested && TransientFailureClassifier.IsTransient(ex);

    private static TimeSpan? NextDelay(int attempt, TimeSpan remaining, bool boundEachAttempt)
    {
        if (remaining <= TimeSpan.Zero) return null;

        var backoff = TimeSpan.FromMilliseconds(Math.Min(InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1), MaxBackoff.TotalMilliseconds));

        if (backoff < remaining) return backoff;

        // A bounded call has no time left for another attempt after this wait; that attempt would run unbounded and could hang.
        return boundEachAttempt ? null : remaining;
    }

    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancellation, CancellationToken stopRetrying)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopRetrying);

        try
        {
            await _delayAsync(delay, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stopRetrying.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            return false;
        }
    }
}
