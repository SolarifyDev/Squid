using Halibut.Queue.Redis.RedisHelpers;
using Squid.Core.Halibut.Resilience;
using StackExchange.Redis;

namespace Squid.Core.Halibut.RedisQueue;

public interface IRedisPing
{
    Task PingAsync(CancellationToken ct);
}

/// <summary>
/// Pings Redis on its own connection, separate from Halibut's, so a Redis that stops answering is noticed within a few seconds even when no cross-pod request
/// is in flight to notice it. The connection is made on the first ping and never blocks startup (<c>AbortOnConnectFail</c> is off), so Redis being down
/// when the server boots is only a failed ping.
/// </summary>
public sealed class StackExchangeRedisPing(string connectionString, TimeSpan timeout) : IRedisPing, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer _connection;

    public async Task PingAsync(CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);

        await connection.GetDatabase().PingAsync().WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    private async Task<ConnectionMultiplexer> ConnectionAsync(CancellationToken ct)
    {
        if (_connection != null) return _connection;

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return _connection ??= await ConnectionMultiplexer.ConnectAsync(PingOptions(), null).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ConfigurationOptions PingOptions()
    {
        var options = ConfigurationOptions.Parse(connectionString);

        options.AbortOnConnectFail = false;
        options.ConnectTimeout = (int)timeout.TotalMilliseconds;
        options.SyncTimeout = (int)timeout.TotalMilliseconds;
        options.AsyncTimeout = (int)timeout.TotalMilliseconds;

        return options;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync().ConfigureAwait(false);

        _gate.Dispose();
    }
}

/// <summary>Reports what Halibut's own Redis connection sees (a lost connection, a failed operation) to the breaker, so an outage opens it without waiting for the next ping.</summary>
public sealed class RedisFacadeHealthObserver(IRedisQueueHealth health) : IRedisFacadeObserver
{
    public void OnRedisConnectionFailed(string endPoint, ConnectionFailureType failureType, Exception exception) => health.RecordFailure($"connection to {endPoint} failed ({failureType})");

    public void OnRedisServerRepliedWithAnErrorMessage(string endPoint, string message)
    {
    }

    public void OnRedisConnectionRestored(string endPoint)
    {
    }

    public void OnRedisOperationFailed(Exception exception, bool willRetry) => health.RecordFailure($"operation failed: {exception.GetType().Name}");
}

/// <summary>
/// The ping loop. It pings while the breaker is closed (to open it on an outage) or half-open (to close it on recovery) and rests while it is open, so a Redis
/// that is down is not hammered. A ping that fails, or takes longer than the timeout, is a failure.
/// </summary>
public sealed class RedisHealthProbe(IRedisQueueHealth health, IRedisPing ping, RedisQueueOptions options) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop;

    public void Start() => _loop ??= Task.Run(RunAsync);

    internal async Task ProbeOnceAsync(CancellationToken ct)
    {
        if (health.State == CircuitBreakerState.Open) return;

        try
        {
            await ping.PingAsync(ct).ConfigureAwait(false);

            health.RecordSuccess();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            health.RecordFailure($"ping failed: {ex.GetType().Name}");
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(options.ProbeInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await ProbeOnceAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        if (_loop != null) await _loop.ConfigureAwait(false);

        _stop.Dispose();
    }
}
