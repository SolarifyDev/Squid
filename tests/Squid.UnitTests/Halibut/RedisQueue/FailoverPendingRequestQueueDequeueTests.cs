using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Halibut;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;
using Shouldly;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Xunit;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

/// <summary>The dequeue side: one poller served from two sources without losing or duplicating a request, and without Redis ever holding the local path back.</summary>
public sealed class FailoverPendingRequestQueueDequeueTests : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly PendingRequestQueueAsync _local = LocalQueue();
    private readonly FakeRedisQueue _redis = new();
    private readonly FakeRedisFactory _factory;
    private readonly FakeHealth _health = new();
    private readonly FailoverPendingRequestQueue _queue;

    public FailoverPendingRequestQueueDequeueTests()
    {
        _factory = new FakeRedisFactory(_redis);
        _queue = new FailoverPendingRequestQueue(Endpoint, _local, _factory, new FailoverQueueContext(_health, Options(), TimeProvider.System, null, PeerPresent()));
    }

    public async ValueTask DisposeAsync() => await _queue.DisposeAsync();

    [Fact]
    public async Task BreakerOpen_DequeueIsPureLocal_AndRedisIsNeverCreated()
    {
        _health.IsAvailable = false;

        var request = Request();
        var queued = _local.QueueAndWaitAsync(request, CancellationToken.None);
        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue");

        next.RequestMessage.Id.ShouldBe(request.Id);
        _factory.Created.ShouldBe(0);
        _redis.DequeueCalls.ShouldBe(0);

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);
        (await queued.WithinAsync(Limit, "queued")).Result.ShouldBe("ok");
    }

    [Fact]
    public async Task BreakerOpen_AnIdleDequeueReturnsAfterTheProbeInterval_SoRecoveredRedisIsListenedToSoon()
    {
        _health.IsAvailable = false;

        await using var queue = new FailoverPendingRequestQueue(Endpoint, LocalQueue(TimeSpan.FromSeconds(30)), _factory, new FailoverQueueContext(_health, Options() with { ProbeInterval = TimeSpan.FromMilliseconds(200) }, TimeProvider.System, null, PeerPresent()));

        var timer = Stopwatch.StartNew();
        var next = await queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue");

        next.ShouldBeNull();
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "Halibut's own wait is 30 seconds");
    }

    [Fact]
    public async Task BreakerOpen_ACallerCancellationStillThrows()
    {
        _health.IsAvailable = false;

        using var cancel = new CancellationTokenSource();
        var polling = _queue.DequeueAsync(cancel.Token);

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => polling);
    }

    [Fact]
    public async Task LocalRequestWins_WhileRedisLegWaits_RedisLegIsNotCancelled()
    {
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var request = Request();
        var queued = _local.QueueAndWaitAsync(request, CancellationToken.None);
        var next = await polling.WithinAsync(Limit, "dequeue");

        next.RequestMessage.Id.ShouldBe(request.Id);
        _redis.DequeueCancelled.ShouldBe(0, "cancelling a Redis dequeue could lose a request it had just popped, so it is never cancelled mid-flight");

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);
        await queued.WithinAsync(Limit, "queued");
        _redis.Applied.ShouldBeEmpty("a locally queued request is answered locally");
    }

    [Fact]
    public async Task RedisRequest_IsServed_AndItsResponseGoesBackThroughRedis()
    {
        var request = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);
        await _redis.Items.Writer.WriteAsync(Dequeued(request));

        var next = await polling.WithinAsync(Limit, "dequeue");

        next.RequestMessage.Id.ShouldBe(request.Id);

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "from the agent"), request.ActivityId);

        _redis.Applied.Single().ActivityId.ShouldBe(request.ActivityId);
    }

    [Fact]
    public async Task RedisRequestArrivingWhileNobodyWaits_IsParked_AndServedByTheNextDequeue()
    {
        var local = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var queued = _local.QueueAndWaitAsync(local, CancellationToken.None);

        (await polling.WithinAsync(Limit, "first dequeue")).RequestMessage.Id.ShouldBe(local.Id);

        // The leg is still waiting; the agent is busy on the local request when the cross-pod one arrives.
        var crossPod = Request();

        await _redis.Items.Writer.WriteAsync(Dequeued(crossPod));
        await Task.Delay(100);
        await _queue.ApplyResponse(ResponseMessage.FromResult(local, "ok"), local.ActivityId);
        await queued.WithinAsync(Limit, "queued");

        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "second dequeue");

        next.RequestMessage.Id.ShouldBe(crossPod.Id);
        _redis.Applied.ShouldBeEmpty("it was delivered, not failed");

        // Answered and then left alone for several liveness windows: the expiry that was watching it must not fail a request that was delivered and answered.
        await _queue.ApplyResponse(ResponseMessage.FromResult(crossPod, "ok"), crossPod.ActivityId);
        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        _redis.Applied.Select(a => a.ActivityId).ShouldBe([crossPod.ActivityId], "exactly one response, the real one");
    }

    [Fact]
    public async Task ParkedRequest_WaitsWhileTheAgentIsBusy_AndIsFailedWhenThePollerIsGone()
    {
        var local = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var queued = _local.QueueAndWaitAsync(local, CancellationToken.None);

        await polling.WithinAsync(Limit, "first dequeue");

        var crossPod = Request();

        await _redis.Items.Writer.WriteAsync(Dequeued(crossPod));

        await Task.Delay(TimeSpan.FromMilliseconds(1000));
        _redis.Applied.ShouldBeEmpty("the agent is busy on a request for three liveness windows; the parked request keeps waiting for its next dequeue");

        await _queue.ApplyResponse(ResponseMessage.FromResult(local, "ok"), local.ActivityId);
        await queued.WithinAsync(Limit, "queued");
        await WaitUntilAsync(() => !_redis.Applied.IsEmpty);

        var (response, id) = _redis.Applied.Single();

        id.ShouldBe(crossPod.ActivityId);
        response.Error.ShouldNotBeNull("the sending pod is told at once, instead of waiting for the pickup timeout");
        response.Error.HalibutErrorType.ShouldBe(typeof(HalibutClientException).FullName, "the client rebuilds it as a plain transport failure, which the pipeline pauses on; an untyped error would be logged as a protocol fault");
    }

    [Fact]
    public async Task CallerCancelsADequeueWaitingOnBothLegs_NothingIsLost()
    {
        using var cancel = new CancellationTokenSource();
        var polling = _queue.DequeueAsync(cancel.Token);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => polling);

        var request = Request();
        var queued = _local.QueueAndWaitAsync(request, CancellationToken.None);
        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue after cancel");

        next.RequestMessage.Id.ShouldBe(request.Id, "the cancelled dequeue took nothing");

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);
        await queued.WithinAsync(Limit, "queued");
    }

    [Fact]
    public async Task EveryRequestFromBothSources_IsHandedOutExactlyOnce_UnderConcurrentPollers()
    {
        const int perSource = 150;
        var seen = new ConcurrentDictionary<string, int>();
        using var stop = new CancellationTokenSource();
        var pollers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => PollAsync(seen, stop.Token))).ToList();

        var localTasks = Enumerable.Range(0, perSource).Select(_ => _local.QueueAndWaitAsync(Request(), CancellationToken.None)).ToList();
        var redisRequests = Enumerable.Range(0, perSource).Select(_ => Request()).ToList();

        foreach (var request in redisRequests)
        {
            await _redis.Items.Writer.WriteAsync(Dequeued(request));
            await Task.Yield();
        }

        await Task.WhenAll(localTasks).WithinAsync(TimeSpan.FromSeconds(30), "local requests");
        await WaitUntilAsync(() => _redis.Applied.Count == perSource, TimeSpan.FromSeconds(30));

        await stop.CancelAsync();
        await Task.WhenAll(pollers).WithinAsync(Limit, "pollers");

        seen.Count.ShouldBe(perSource * 2, "no request was lost");
        seen.Values.ShouldAllBe(count => count == 1, "no request was handed out twice");
        redisRequests.ShouldAllBe(r => seen.ContainsKey(r.Id));
        _redis.Applied.Select(a => a.ActivityId).Distinct().Count().ShouldBe(perSource);
        _redis.DequeueCancelled.ShouldBe(0);
    }

    private async Task PollAsync(ConcurrentDictionary<string, int> seen, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var next = await _queue.DequeueAsync(ct);

                if (next == null) continue;

                seen.AddOrUpdate(next.RequestMessage.Id, 1, (_, n) => n + 1);

                await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "ok"), next.RequestMessage.ActivityId);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task ARedisRequestThatWasHandedOut_IsNotRetainedAfterItIsAnswered_WhileAPollerStaysConnected()
    {
        var polling = _queue.DequeueAsync(CancellationToken.None);
        var references = new List<WeakReference>();

        for (var i = 0; i < 20; i++)
        {
            references.Add(await HandOutAndAnswerAsync(polling));

            polling = _queue.DequeueAsync(CancellationToken.None);
        }

        // The last dequeue is still outstanding: the poller is live for every one of the windows below.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        CollectGarbage();

        references.Count(r => r.IsAlive).ShouldBe(0, "a claimed request must not be kept alive by the expiry that was watching it");
        _redis.Applied.Count.ShouldBe(20, "every request was answered once, none failed by the expiry");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> HandOutAndAnswerAsync(Task<RequestMessageWithCancellationToken> polling)
    {
        var request = Request();

        await WaitUntilAsync(() => _redis.DequeueCalls >= 1);
        await _redis.Items.Writer.WriteAsync(Dequeued(request));

        var next = await polling.WithinAsync(Limit, "dequeue");

        await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "ok"), next.RequestMessage.ActivityId);

        return new WeakReference(request);
    }

    private static void CollectGarbage()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public async Task ParkedRequestCancelledByItsRequester_IsNotHandedOut_AndItsOriginIsToldSo()
    {
        using var cancelled = new CancellationTokenSource();
        var crossPod = Request();
        var (local, queued) = await ParkAsync(new RequestMessageWithCancellationToken(crossPod, cancelled.Token));

        await cancelled.CancelAsync();
        await _queue.ApplyResponse(ResponseMessage.FromResult(local, "ok"), local.ActivityId);
        await queued.WithinAsync(Limit, "queued");

        var other = Request();
        var otherQueued = _local.QueueAndWaitAsync(other, CancellationToken.None);
        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue");

        next.RequestMessage.Id.ShouldBe(other.Id, "the cancelled request must not be sent to the agent: Halibut would drop the connection");
        _redis.Applied.Select(a => a.ActivityId).ShouldContain(crossPod.ActivityId, "the sending pod is told so that it releases the request");

        await _queue.ApplyResponse(ResponseMessage.FromResult(other, "ok"), other.ActivityId);
        await otherQueued.WithinAsync(Limit, "other queued");
    }

    [Fact]
    public async Task DequeueWhoseOwnTokenIsAlreadyCancelled_TakesNoParkedRequest()
    {
        var crossPod = Request();
        var (local, queued) = await ParkAsync(Dequeued(crossPod));

        await _queue.ApplyResponse(ResponseMessage.FromResult(local, "ok"), local.ActivityId);
        await queued.WithinAsync(Limit, "queued");

        using var cancel = new CancellationTokenSource();

        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => _queue.DequeueAsync(cancel.Token));

        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue");

        next.RequestMessage.Id.ShouldBe(crossPod.Id, "the cancelled dequeue took nothing, so the item is still there");
    }

    /// <summary>Leaves a Redis item parked: the agent is busy on a local request when it arrives. The local request is returned, still unanswered.</summary>
    private async Task<(RequestMessage Local, Task<ResponseMessage> Queued)> ParkAsync(RequestMessageWithCancellationToken item)
    {
        var local = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var queued = _local.QueueAndWaitAsync(local, CancellationToken.None);

        await polling.WithinAsync(Limit, "first dequeue");
        await _redis.Items.Writer.WriteAsync(item);
        await Task.Delay(100);

        return (local, queued);
    }

    [Fact]
    public async Task RedisLegFailingSlowly_NeverSlowsTheLocalPath()
    {
        _redis.DequeueFailure = new InvalidOperationException("Redis is down");
        _redis.DequeueFailureDelay = TimeSpan.FromSeconds(30);

        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var request = Request();
        var timer = Stopwatch.StartNew();
        var queued = _local.QueueAndWaitAsync(request, CancellationToken.None);
        var next = await polling.WithinAsync(Limit, "dequeue");

        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), "Halibut's Redis queue sleeps up to 30 s before it reports an error; that sleep is on the Redis leg only");
        next.RequestMessage.Id.ShouldBe(request.Id);

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);
        await queued.WithinAsync(Limit, "queued");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task RedisLegFailure_CountsAgainstTheBreaker_UnlessTheMessageItselfWasUnreadable(bool unreadable, int expectedFailures)
    {
        _redis.DequeueFailure = unreadable ? new QueueMessageUnreadableException("tampered") : new InvalidOperationException("Redis is down");

        var request = Request();
        var queued = _local.QueueAndWaitAsync(request, CancellationToken.None);

        await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue");
        await WaitUntilAsync(() => _redis.DequeueCalls == 1);
        await Task.Delay(100);

        _health.Failures.Count.ShouldBe(expectedFailures);

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);
        await queued.WithinAsync(Limit, "queued");
    }

    [Fact]
    public async Task ApplyingACrossPodResponseFails_DoesNotThrowIntoHalibutsPollingLoop_AndCountsAgainstTheBreaker()
    {
        var request = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);
        await _redis.Items.Writer.WriteAsync(Dequeued(request));
        await polling.WithinAsync(Limit, "dequeue");

        _redis.ApplyFailure = new InvalidOperationException("Redis went away");

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);

        _health.Failures.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_FailsParkedRequests_AndDisposesBothQueues()
    {
        var local = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var queued = _local.QueueAndWaitAsync(local, CancellationToken.None);

        await polling.WithinAsync(Limit, "dequeue");

        var parked = Request();

        await _redis.Items.Writer.WriteAsync(Dequeued(parked));
        await Task.Delay(100);
        await _queue.DisposeAsync();

        _redis.Applied.Single().ActivityId.ShouldBe(parked.ActivityId);
        _redis.Disposed.ShouldBeTrue();

        _ = queued;
    }

    [Fact]
    public async Task Dispose_WithARedisThatNeverAnswers_FinishesWithinTheBudget()
    {
        var local = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var queued = _local.QueueAndWaitAsync(local, CancellationToken.None);

        await polling.WithinAsync(Limit, "dequeue");
        await _redis.Items.Writer.WriteAsync(Dequeued(Request()));
        await Task.Delay(100);

        _redis.Hang = true;

        var timer = Stopwatch.StartNew();

        await _queue.DisposeAsync().AsTask().WithinAsync(TimeSpan.FromSeconds(20), "dispose");

        timer.Elapsed.ShouldBeLessThan(FailoverPendingRequestQueue.DisposeBudget + TimeSpan.FromSeconds(3), "shutting down must not wait out a Redis timeout of tens of seconds");
        _redis.Disposed.ShouldBeTrue("the Redis queue is released even though failing the parked request is stuck");

        _ = queued;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(5));

        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never became true");

            await Task.Delay(10);
        }
    }
}
