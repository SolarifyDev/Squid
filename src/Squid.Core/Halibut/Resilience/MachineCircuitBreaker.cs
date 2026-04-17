namespace Squid.Core.Halibut.Resilience;

/// <summary>
/// Simple per-machine circuit breaker. Thread-safe via a single lock — the
/// state transitions are cheap (no allocations, no async) so lock contention
/// is negligible even under high concurrent probe/observe rates.
///
/// Semantics:
///   Closed: every call is allowed. Successive failures increment the counter;
///           reaching <see cref="_failureThreshold"/> moves to Open.
///   Open:   every call throws <see cref="CircuitOpenException"/> until
///           <see cref="_openDuration"/> has elapsed since opening. Then moves
///           to HalfOpen on the next caller.
///   HalfOpen: one call is allowed through as a probe. Success closes the
///             breaker and zeros the counter; failure re-opens it.
/// </summary>
public sealed class MachineCircuitBreaker
{
    private readonly int _machineId;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();

    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;

    public MachineCircuitBreaker(int machineId, int failureThreshold, TimeSpan openDuration, Func<DateTimeOffset> clock = null)
    {
        if (failureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        if (openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(openDuration));

        _machineId = machineId;
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public CircuitBreakerState State
    {
        get
        {
            lock (_sync)
            {
                ReevaluateStateForHalfOpen();
                return _state;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get { lock (_sync) return _consecutiveFailures; }
    }

    /// <summary>Throws <see cref="CircuitOpenException"/> if the breaker is currently refusing calls.</summary>
    public void ThrowIfOpen()
    {
        lock (_sync)
        {
            ReevaluateStateForHalfOpen();
            if (_state == CircuitBreakerState.Open)
                throw new CircuitOpenException(_machineId, _openedAt + _openDuration);
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
            _state = CircuitBreakerState.Closed;
        }
    }

    public void RecordFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;

            if (_state == CircuitBreakerState.HalfOpen)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = _clock();
                return;
            }

            if (_consecutiveFailures >= _failureThreshold && _state == CircuitBreakerState.Closed)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = _clock();
            }
        }
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        ThrowIfOpen();

        try
        {
            var result = await action().ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (CircuitOpenException) { throw; }
        catch
        {
            RecordFailure();
            throw;
        }
    }

    private void ReevaluateStateForHalfOpen()
    {
        if (_state != CircuitBreakerState.Open) return;
        if (_clock() - _openedAt < _openDuration) return;

        _state = CircuitBreakerState.HalfOpen;
    }
}
