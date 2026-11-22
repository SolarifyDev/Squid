namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// The single per-RPC retry loop for agent calls that outlive a dropped connection (script GetStatus /
/// Cancel / Complete). A call is retried only while the failure is TRANSIENT per
/// <see cref="TransientFailureClassifier"/> (never a second classification), only inside a total time
/// budget, with exponential backoff, and never once the caller's token fires. When the budget runs out
/// (or the failure is not transient) the exception the agent call actually threw is rethrown unchanged,
/// so every downstream classifier (the runner's transient-pause path, the circuit breaker) sees exactly
/// what it saw before retries existed.
/// </summary>
public sealed class RpcRetryHandler
{
    public const string RetryBudgetSecondsEnvVar = "SQUID_HALIBUT_RPC_RETRY_BUDGET_SECONDS";
    public const string CompleteRetryBudgetSecondsEnvVar = "SQUID_HALIBUT_RPC_COMPLETE_RETRY_BUDGET_SECONDS";
    public const string CancelRetryBudgetSecondsEnvVar = "SQUID_HALIBUT_RPC_CANCEL_RETRY_BUDGET_SECONDS";

    internal const int DefaultRetryBudgetSeconds = 120;
    internal const int DefaultCompleteRetryBudgetSeconds = 30;
    internal const int DefaultCancelRetryBudgetSeconds = 20;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public RpcRetryHandler(TimeSpan statusBudget, TimeSpan completeBudget, TimeSpan cancelBudget) : this(statusBudget, completeBudget, cancelBudget, null, null) { }

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
    /// Shorter budget for CancelScript: a user cancel or script-timeout cancel to a dead agent keeps the
    /// task in Cancelling while it retries, so cancel latency must stay bounded.
    /// </summary>
    public TimeSpan CancelBudget { get; }

    public static RpcRetryHandler FromEnvironment()
        => new(ReadBudget(RetryBudgetSecondsEnvVar, DefaultRetryBudgetSeconds), ReadBudget(CompleteRetryBudgetSecondsEnvVar, DefaultCompleteRetryBudgetSeconds), ReadBudget(CancelRetryBudgetSecondsEnvVar, DefaultCancelRetryBudgetSeconds));

    /// <summary>Non-negative integer seconds; 0 disables retries; unset/garbage/negative falls back to the default.</summary>
    internal static TimeSpan ParseBudget(string raw, int defaultSeconds)
        => int.TryParse(raw?.Trim(), out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(defaultSeconds);

    private static TimeSpan ReadBudget(string envVar, int defaultSeconds)
        => ParseBudget(System.Environment.GetEnvironmentVariable(envVar), defaultSeconds);

    /// <param name="cancellation">Caller cancellation: aborts the backoff wait with <see cref="OperationCanceledException"/>.</param>
    /// <param name="stopRetrying">Stops further retries and rethrows the last real failure (e.g. the liveness probe already declared the agent gone).</param>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> call, string rpcName, TimeSpan budget, CancellationToken cancellation, CancellationToken stopRetrying = default)
    {
        var started = _utcNow();
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex, cancellation, stopRetrying))
            {
                var delay = NextDelay(attempt, budget - (_utcNow() - started));

                if (delay == null) throw;

                Log.Warning(ex, "[Deploy] Agent RPC {Rpc} failed transiently (attempt {Attempt}); retrying in {DelayMs}ms", rpcName, attempt, (int)delay.Value.TotalMilliseconds);

                if (!await WaitAsync(delay.Value, cancellation, stopRetrying).ConfigureAwait(false)) throw;
            }
        }
    }

    private static bool IsRetryable(Exception ex, CancellationToken cancellation, CancellationToken stopRetrying)
        => !cancellation.IsCancellationRequested && !stopRetrying.IsCancellationRequested && TransientFailureClassifier.IsTransient(ex);

    private static TimeSpan? NextDelay(int attempt, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return null;

        var backoff = TimeSpan.FromMilliseconds(Math.Min(InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1), MaxBackoff.TotalMilliseconds));

        return backoff < remaining ? backoff : remaining;
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
