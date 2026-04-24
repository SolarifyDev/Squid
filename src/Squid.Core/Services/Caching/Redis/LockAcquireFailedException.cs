namespace Squid.Core.Services.Caching.Redis;

/// <summary>
/// Thrown when <see cref="IRedisSafeRunner.ExecuteWithLockAsync{T}"/> could
/// not even attempt to acquire a distributed lock because of infrastructure
/// failure — Redis unreachable, TLS handshake refused, network partition,
/// multiplexer disposed, etc.
///
/// <para>Deliberately DISTINCT from the contention path (lock already held
/// by another caller). Pre-1.6.x, both conditions collapsed into a
/// <c>null</c> return from <c>ExecuteWithLockAsync&lt;T&gt;</c>, so callers
/// could not tell them apart — a Redis outage surfaced to operators as
/// "Machine is currently being upgraded by another request" (the
/// contention message), hiding the actual infrastructure problem and
/// leading to useless retry storms.</para>
///
/// <para>After this fix: contention still returns <c>null</c> (unchanged
/// contract), but infrastructure failure throws this typed exception so
/// callers can surface an accurate error to the operator and log-correlate
/// the Redis outage. Retry guidance differs:
/// <list type="bullet">
///   <item><b>Contention</b>: retry shortly — the other caller typically
///   completes within 2 minutes.</item>
///   <item><b>Infrastructure failure</b>: do NOT retry — wait for Redis
///   connectivity to recover (operator ticket, not user retry).</item>
/// </list></para>
/// </summary>
public sealed class LockAcquireFailedException : Exception
{
    public string LockKey { get; }

    public LockAcquireFailedException(string lockKey, Exception innerException)
        : base($"Could not acquire distributed lock '{lockKey}': {innerException.GetType().Name}: {innerException.Message}", innerException)
    {
        LockKey = lockKey;
    }
}
