using Halibut.Diagnostics;
using Halibut.Queue;
using Halibut.Queue.QueuedDataStreams;
using Halibut.Queue.Redis;
using Halibut.Queue.Redis.RedisDataLossDetection;
using Halibut.Queue.Redis.RedisHelpers;
using Halibut.ServiceModel;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>Builds the queue of each polling agent endpoint: Halibut's in-memory queue first, Redis behind it (see <see cref="FailoverPendingRequestQueue"/>). Only polling endpoints ever reach a queue factory: a listening Tentacle is called over a direct connection.</summary>
public sealed class FailoverPendingRequestQueueFactory(HalibutTimeoutsAndLimits limits, ILogFactory logFactory, IPendingRequestQueueFactory redisFactory, FailoverQueueContext context) : IPendingRequestQueueFactory
{
    public IPendingRequestQueue CreateQueue(Uri endpoint)
        => new FailoverPendingRequestQueue(endpoint, new PendingRequestQueueAsync(limits, logFactory.ForEndpoint(endpoint)), redisFactory, context);
}

/// <summary>Everything the Redis side of the queue needs, gathered once so the backend is built in one place.</summary>
public sealed record RedisQueueBackendInputs(string ConnectionString, RedisQueueOptions Options, IRedisQueueHealth Health, QueueMessageSerializer Serializer, IStoreDataStreamsForDistributedQueues DataStreams, HalibutTimeoutsAndLimits Limits, ILogFactory LogFactory);

/// <summary>
/// Halibut's Redis queue with its connection, data-loss watch and the health ping that guards it. Made on first use, so a server that never needs Redis never
/// connects to it, and a failure to make it (no connection string configured) is the Redis leg's problem alone: it is retried on the next use, never cached.
/// </summary>
public sealed class LazyRedisPendingRequestQueueFactory(Func<RedisQueueBackend> create) : IPendingRequestQueueFactory, IAsyncDisposable
{
    private readonly object _gate = new();
    private RedisQueueBackend _backend;

    public IPendingRequestQueue CreateQueue(Uri endpoint) => Backend().CreateQueue(endpoint);

    private RedisQueueBackend Backend()
    {
        lock (_gate) return _backend ??= create();
    }

    public async ValueTask DisposeAsync()
    {
        RedisQueueBackend backend;

        lock (_gate) backend = _backend;

        if (backend != null) await backend.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class RedisQueueBackend : IPendingRequestQueueFactory, IAsyncDisposable
{
    private readonly RedisFacade _facade;
    private readonly WatchForRedisLosingAllItsData _dataLossWatch;
    private readonly RedisPendingRequestQueueFactory _queues;
    private readonly StackExchangeRedisPing _ping;
    private readonly RedisHealthProbe _probe;

    public RedisQueueBackend(RedisQueueBackendInputs inputs)
    {
        if (string.IsNullOrWhiteSpace(inputs.ConnectionString))
            throw new InvalidOperationException("RedisCacheConnectionString is not configured, so requests cannot be queued across server pods");

        var log = inputs.LogFactory.ForPrefix("redis-queue");

        _facade = new RedisFacade(inputs.ConnectionString, inputs.Options.KeyPrefix, log, new RedisFacadeHealthObserver(inputs.Health));
        _dataLossWatch = new WatchForRedisLosingAllItsData(_facade, log);
        _queues = new RedisPendingRequestQueueFactory(inputs.Serializer, inputs.DataStreams, _dataLossWatch, new HalibutRedisTransport(_facade), inputs.Limits, inputs.LogFactory);
        _ping = new StackExchangeRedisPing(inputs.ConnectionString, inputs.Options.ProbeTimeout);
        _probe = new RedisHealthProbe(inputs.Health, _ping, inputs.Options);

        _probe.Start();
    }

    public IPendingRequestQueue CreateQueue(Uri endpoint) => _queues.CreateQueue(endpoint);

    public async ValueTask DisposeAsync()
    {
        await _probe.DisposeAsync().ConfigureAwait(false);
        await _ping.DisposeAsync().ConfigureAwait(false);
        await _dataLossWatch.DisposeAsync().ConfigureAwait(false);
        await _facade.DisposeAsync().ConfigureAwait(false);
    }
}
