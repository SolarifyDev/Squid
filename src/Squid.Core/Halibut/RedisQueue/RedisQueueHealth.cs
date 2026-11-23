using Squid.Core.Halibut.Resilience;

namespace Squid.Core.Halibut.RedisQueue;

public interface IRedisQueueHealth
{
    /// <summary>True only while the breaker is closed. Half-open means "being probed", not "usable": only the ping moves it, never a user request.</summary>
    bool IsAvailable { get; }

    CircuitBreakerState State { get; }

    void RecordFailure(string source);

    void RecordSuccess();
}

/// <summary>
/// Whether the shared Redis is usable, as a breaker: consecutive failures (a failed ping, a Redis connection or operation error, a failed cross-pod send)
/// open it, cross-pod requests then fail fast instead of waiting on Halibut's 30 second Redis retries, and a successful ping after the open duration closes it again.
/// </summary>
public sealed class RedisQueueHealth : IRedisQueueHealth
{
    private readonly MachineCircuitBreaker _breaker;

    public RedisQueueHealth(RedisQueueOptions options, TimeProvider clock = null)
    {
        var time = clock ?? TimeProvider.System;

        _breaker = new MachineCircuitBreaker(0, options.FailureThreshold, options.OpenDuration, time.GetUtcNow);
    }

    public bool IsAvailable => _breaker.State == CircuitBreakerState.Closed;

    public CircuitBreakerState State => _breaker.State;

    public void RecordFailure(string source)
    {
        var before = _breaker.State;

        _breaker.RecordFailure();

        if (before != CircuitBreakerState.Open && _breaker.State == CircuitBreakerState.Open)
            Log.Warning("Redis queue breaker opened after {Failures} consecutive failures (last: {Source}); requests for agents polling another pod fail fast until Redis answers again", _breaker.ConsecutiveFailures, source);
    }

    public void RecordSuccess()
    {
        var before = _breaker.State;

        _breaker.RecordSuccess();

        if (before != CircuitBreakerState.Closed)
            Log.Information("Redis queue breaker closed: Redis answers again");
    }
}
