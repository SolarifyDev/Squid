using System.Collections.Concurrent;
using System.Threading.Channels;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>
/// What a queue shares with every other queue of the server: whether Redis is usable, the tunables, the clock and whether another replica exists. A context without
/// <paramref name="Replicas"/> has no other replica, so its queues never use Redis.
/// </summary>
public sealed record FailoverQueueContext(IRedisQueueHealth Health, RedisQueueOptions Options, TimeProvider Clock, RedisFallbackLog Fallback = null, IReplicaPresence Replicas = null);

/// <summary>
/// The pending-request queue of ONE polling agent on ONE pod, in two legs. The local leg is Halibut's in-memory queue: a request for an agent whose poller is
/// attached to this pod goes there and never touches Redis, so a single-replica install has no Redis in its request path. The Redis leg is Halibut's Redis
/// queue: a request for an agent with no live poller here goes through it, so the pod that does hold the poller collects it, but only while another server
/// replica is alive: with none, Redis is not touched at all. The dequeue side serves both
/// legs: the local leg is cancel-safe (an item is either returned or untouched), the Redis leg is never cancelled mid-flight (a cancelled Redis dequeue could
/// lose a request it had just popped), so the two are raced and a Redis item that arrives while no dequeue is waiting is parked and handed to the next one.
/// Redis trouble is confined to the Redis leg: it is created on first use, runs on its own task, and is skipped while the breaker is open.
/// </summary>
public sealed partial class FailoverPendingRequestQueue : IPendingRequestQueue
{
    private readonly Uri _endpoint;
    private readonly IPendingRequestQueue _local;
    private readonly IPendingRequestQueueFactory _redisFactory;
    private readonly IRedisQueueHealth _health;
    private readonly PollerPresence _presence;
    private readonly IReplicaPresence _replicas;
    private readonly TimeProvider _clock;
    private readonly RedisFallbackLog _fallback;
    private readonly RedisQueueOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Channel<ParkedRequest> _parked = Channel.CreateUnbounded<ParkedRequest>();
    private readonly ConcurrentDictionary<Guid, IPendingRequestQueue> _origins = new();
    private int _onRedis;
    private CancellationTokenSource _redisRequestQueued = new();
    private IPendingRequestQueue _redis;
    private Task _redisLeg;
    private bool _disposed;

    internal static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(3);

    public FailoverPendingRequestQueue(Uri endpoint, IPendingRequestQueue local, IPendingRequestQueueFactory redisFactory, FailoverQueueContext context)
    {
        _endpoint = endpoint;
        _local = local;
        _redisFactory = redisFactory;
        _health = context.Health;
        _options = context.Options;
        _clock = context.Clock;
        _fallback = context.Fallback ?? new RedisFallbackLog(context.Clock);
        _replicas = context.Replicas ?? new ReplicaPresence();
        _presence = new PollerPresence(context.Clock, context.Options.LivenessWindow);
    }

    public bool IsEmpty => Count == 0;

    public int Count => _local.Count + (_redis?.Count ?? 0);

    internal bool PollerIsLive => _presence.IsLive;

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _local.DisposeAsync().ConfigureAwait(false);

        await RunWithinDisposeBudgetAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releasing the Redis leg is the one part of shutdown that can wait on Redis: a dequeue, a response written to a dead connection and Halibut's own queue
    /// disposal each sit in StackExchange.Redis's backlog until its (here 50 second) timeout. So it gets a short budget, none at all while the breaker already says
    /// Redis is down, and is then abandoned to finish by itself, instead of holding up the shutdown of a server (one agent after another) whose Redis is down.
    /// </summary>
    private async Task RunWithinDisposeBudgetAsync()
    {
        var budget = _health.IsAvailable ? DisposeBudget : TimeSpan.Zero;

        try
        {
            await Task.Run(() => ReleaseRedisLegAsync(budget)).WaitAsync(budget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warning("Releasing the Redis queue of agent {Endpoint} was not finished within {Budget}; leaving it to finish in the background", _endpoint, budget);
        }
    }

    private async Task ReleaseRedisLegAsync(TimeSpan budget)
    {
        if (_redisLeg != null) await Task.WhenAny(_redisLeg, Task.Delay(budget)).ConfigureAwait(false);

        // Together, so a response that cannot be written to a dead Redis does not keep the queue itself from being released.
        await Task.WhenAll(FailParkedRequestsAsync("the server pod holding the agent's connection is shutting down"), DisposeRedisQueueAsync()).ConfigureAwait(false);
    }

    private async Task DisposeRedisQueueAsync()
    {
        if (_redis != null) await _redis.DisposeAsync().ConfigureAwait(false);
    }
}
