using System.Collections.Concurrent;
using System.Threading.Channels;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.Resilience;

namespace Squid.UnitTests.Halibut.RedisQueue;

/// <summary>A clock the test moves by hand. Timers are not needed by the code under test that uses it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
}

internal sealed class FakeHealth : IRedisQueueHealth
{
    private readonly ConcurrentQueue<string> _failures = new();

    public bool IsAvailable { get; set; } = true;

    public CircuitBreakerState State => IsAvailable ? CircuitBreakerState.Closed : CircuitBreakerState.Open;

    public IReadOnlyCollection<string> Failures => _failures;

    public void RecordFailure(string source) => _failures.Enqueue(source);

    public void RecordSuccess()
    {
    }
}

/// <summary>Stands for Halibut's Redis queue: items come out of a channel, and every call is counted. Like the real one, a dequeue that is cancelled throws.</summary>
internal sealed class FakeRedisQueue : IPendingRequestQueue
{
    private int _dequeueCalls;
    private int _dequeueCancelled;

    public Channel<RequestMessageWithCancellationToken> Items { get; } = Channel.CreateUnbounded<RequestMessageWithCancellationToken>();

    public ConcurrentQueue<(ResponseMessage Response, Guid ActivityId)> Applied { get; } = new();

    public Func<RequestMessage, Task<ResponseMessage>> OnQueueAndWait { get; set; } = request => Task.FromResult(ResponseMessage.FromResult(request, "redis"));

    public Exception ApplyFailure { get; set; }

    /// <summary>Like a Redis connection that is gone: writing a response and releasing the queue wait on a timeout that is far away.</summary>
    public bool Hang { get; set; }

    public Exception DequeueFailure { get; set; }

    public TimeSpan DequeueFailureDelay { get; set; }

    public TimeSpan DequeueWait { get; set; } = TimeSpan.FromSeconds(10);

    public int DequeueCalls => Volatile.Read(ref _dequeueCalls);

    public int DequeueCancelled => Volatile.Read(ref _dequeueCancelled);

    public int QueuedCount;

    public bool Disposed { get; private set; }

    public bool IsEmpty => Count == 0;

    public int Count => 0;

    public async Task ApplyResponse(ResponseMessage response, Guid requestActivityId)
    {
        if (Hang) await Task.Delay(Timeout.Infinite);

        if (ApplyFailure != null) throw ApplyFailure;

        Applied.Enqueue((response, requestActivityId));
    }

    public async Task<RequestMessageWithCancellationToken> DequeueAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _dequeueCalls);

        if (DequeueFailure != null)
        {
            // Like Halibut's Redis queue when Redis is down: it sleeps before it throws.
            await Task.Delay(DequeueFailureDelay, cancellationToken);

            throw DequeueFailure;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        linked.CancelAfter(DequeueWait);

        try
        {
            if (await Items.Reader.WaitToReadAsync(linked.Token) && Items.Reader.TryRead(out var item)) return item;

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _dequeueCancelled);

            throw;
        }
    }

    public Task<ResponseMessage> QueueAndWaitAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref QueuedCount);

        return OnQueueAndWait(request);
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;

        if (Hang) await Task.Delay(Timeout.Infinite);
    }
}

internal sealed class FakeRedisFactory(FakeRedisQueue queue) : IPendingRequestQueueFactory
{
    private int _created;

    public Exception CreateFailure { get; set; }

    public int Created => Volatile.Read(ref _created);

    public IPendingRequestQueue CreateQueue(Uri endpoint)
    {
        Interlocked.Increment(ref _created);

        if (CreateFailure != null) throw CreateFailure;

        return queue;
    }
}

internal static class RedisQueueTestSupport
{
    public static readonly Uri Endpoint = new("poll://redis-queue-agent/");

    /// <summary>Another replica is alive, as in a multi-replica install: the only situation in which a request may go through Redis.</summary>
    public static ReplicaPresence PeerPresent()
    {
        var presence = new ReplicaPresence();

        presence.Set(true);

        return presence;
    }

    public static RedisQueueOptions Options(TimeSpan? livenessWindow = null) => new("test", livenessWindow ?? TimeSpan.FromMilliseconds(300), 3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10));

    public static RequestMessage Request(string name = "GetStatus", TimeSpan? pickupTimeout = null)
    {
        var limits = HalibutTimeoutsAndLimits.RecommendedValues();

        limits.PollingRequestQueueTimeout = pickupTimeout ?? TimeSpan.FromSeconds(20);

        return new RequestMessage
        {
            Id = $"{name}-{Guid.NewGuid():N}",
            ActivityId = Guid.NewGuid(),
            Destination = new ServiceEndPoint(Endpoint, "thumbprint", limits),
            ServiceName = "IScriptService",
            MethodName = name,
            Params = []
        };
    }

    public static PendingRequestQueueAsync LocalQueue(TimeSpan? pollingWait = null) => new(new LogFactory().ForEndpoint(Endpoint), pollingWait ?? TimeSpan.FromSeconds(10));

    public static RequestMessageWithCancellationToken Dequeued(RequestMessage request) => new(request, CancellationToken.None);

    public static async Task<T> WithinAsync<T>(this Task<T> task, TimeSpan limit, string what)
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what} did not finish within {limit}");
        }
    }

    public static async Task WithinAsync(this Task task, TimeSpan limit, string what)
    {
        try
        {
            await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what} did not finish within {limit}");
        }
    }
}
