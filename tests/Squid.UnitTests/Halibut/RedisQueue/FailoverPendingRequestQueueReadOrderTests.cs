using System.Reflection;
using Halibut.ServiceModel;
using Shouldly;
using Squid.Core.Halibut.RedisQueue;
using Xunit;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

/// <summary>
/// Pins the documented contract of the dequeue's two Redis reads: the <c>RedisRequestQueued</c> token is read BEFORE <c>HasRequestsOnRedis</c>. A request this pod puts on
/// Redis between the two reads must either be seen by the count or wake the token that was already read; with the reads swapped it does neither, and the dequeue sleeps
/// through Halibut's whole local polling wait although a request is on Redis.
/// </summary>
public sealed class FailoverPendingRequestQueueReadOrderTests : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly FakeRedisQueue _redis = new();
    private readonly FailoverPendingRequestQueue _queue;
    private readonly TaskCompletionSource<global::Halibut.Transport.Protocol.ResponseMessage> _neverAnswered = new();

    public FailoverPendingRequestQueueReadOrderTests()
    {
        _redis.OnQueueAndWait = _ => _neverAnswered.Task;
        _queue = new FailoverPendingRequestQueue(Endpoint, LocalQueue(TimeSpan.FromSeconds(60)), new FakeRedisFactory(_redis), new FailoverQueueContext(new FakeHealth(), Options(), TimeProvider.System, null, new ReplicaPresence()));
    }

    public async ValueTask DisposeAsync()
    {
        _neverAnswered.TrySetCanceled();
        await _queue.DisposeAsync();
    }

    [Fact]
    public async Task ARequestPutOnRedisBetweenTheTwoReads_EndsTheLocalWait_InsteadOfBeingMissed()
    {
        var hookRuns = 0;

        _queue.AfterRedisRequestCountRead = () =>
        {
            if (Interlocked.Increment(ref hookRuns) == 1) PutARequestOnRedis();
        };

        var polling = _queue.DequeueAsync(CancellationToken.None);

        (await polling.WithinAsync(Limit, "dequeue: the wake-up of the token read first must end the wait")).ShouldBeNull("an ordinary empty answer, so the next dequeue listens to Redis");
        hookRuns.ShouldBe(1, "the seam between the two reads was never reached, so this test proved nothing");
    }

    // Stands for the pod's own request leaving through Redis: counted, and the token swapped, exactly as QueueThroughRedisAsync does it, without the routing checks
    // (a poller is attached during a dequeue, so the public path would queue it locally).
    private void PutARequestOnRedis()
    {
        var method = typeof(FailoverPendingRequestQueue).GetMethod("QueueOnRedisAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        _ = (Task)method.Invoke(_queue, [_redis, Request(), CancellationToken.None])!;
    }
}
