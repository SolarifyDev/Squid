using System.IO;
using Halibut;
using Halibut.Queue.Redis.Exceptions;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;
using Shouldly;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.Resilience;
using Xunit;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

/// <summary>The enqueue side: where a request goes, and what happens to it when Redis cannot take it.</summary>
public sealed class FailoverPendingRequestQueueRoutingTests : IAsyncDisposable
{
    private readonly PendingRequestQueueAsync _local = LocalQueue();
    private readonly FakeRedisQueue _redis = new();
    private readonly FakeRedisFactory _factory;
    private readonly FakeHealth _health = new();
    private readonly FailoverPendingRequestQueue _queue;

    public FailoverPendingRequestQueueRoutingTests()
    {
        _factory = new FakeRedisFactory(_redis);
        _queue = new FailoverPendingRequestQueue(Endpoint, _local, _factory, new FailoverQueueContext(_health, Options(), TimeProvider.System));
    }

    public async ValueTask DisposeAsync() => await _queue.DisposeAsync();

    [Fact]
    public async Task NoPollerHasEverBeenSeen_RequestGoesThroughRedis()
    {
        var response = await _queue.QueueAndWaitAsync(Request(), CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "queue");

        response.Result.ShouldBe("redis");
        _redis.QueuedCount.ShouldBe(1);
        _factory.Created.ShouldBe(1);
    }

    [Fact]
    public async Task PollerWaitingOnThisPod_RequestStaysLocal_AndRedisIsNeverCreated()
    {
        var request = Request();
        var poller = Task.Run(async () =>
        {
            var next = await _queue.DequeueAsync(CancellationToken.None);

            await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "local"), next.RequestMessage.ActivityId);
        });

        await WaitUntilAsync(() => _queue.PollerIsLive);

        var response = await _queue.QueueAndWaitAsync(request, CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "queue");
        await poller.WithinAsync(TimeSpan.FromSeconds(5), "poller");

        response.Result.ShouldBe("local");
        _redis.QueuedCount.ShouldBe(0, "a request for an agent polling this pod never touches Redis");
    }

    [Fact]
    public async Task PollerBusyOnARequest_NewRequestStillStaysLocal()
    {
        var first = Request("StartScript");
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _queue.PollerIsLive);

        var firstDone = _queue.QueueAndWaitAsync(first, CancellationToken.None);

        // The poller is attached and now busy: it is between a hand-out and its response.
        var handedOut = await polling.WithinAsync(TimeSpan.FromSeconds(5), "dequeue");

        handedOut.RequestMessage.Id.ShouldBe(first.Id);

        var second = _queue.QueueAndWaitAsync(Request("GetStatus"), CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(700));

        _redis.QueuedCount.ShouldBe(0, "an agent busy on a long request is still attached to this pod, even past the liveness window");

        await _queue.ApplyResponse(ResponseMessage.FromResult(first, "done"), first.ActivityId);
        await firstDone.WithinAsync(TimeSpan.FromSeconds(5), "first");

        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "second dequeue");

        await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "second"), next.RequestMessage.ActivityId);
        (await second.WithinAsync(TimeSpan.FromSeconds(5), "second")).Result.ShouldBe("second");
    }

    [Fact]
    public async Task PollerLeftLongAgo_RequestGoesThroughRedis()
    {
        using var attached = new CancellationTokenSource();
        var poll = _queue.DequeueAsync(attached.Token);

        await attached.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);

        await WaitUntilAsync(() => !_queue.PollerIsLive, TimeSpan.FromSeconds(3));

        await _queue.QueueAndWaitAsync(Request(), CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "queue");

        _redis.QueuedCount.ShouldBe(1);
    }

    // Redis trouble must never fail a request the local queue could serve: with no live poller and no usable Redis the request waits locally, as it did before Redis was involved.
    [Fact]
    public async Task RedisBreakerOpen_NoLivePoller_RequestWaitsLocally_AndIsServedWhenTheAgentConnects()
    {
        _health.IsAvailable = false;

        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        await Task.Delay(100);
        queued.IsCompleted.ShouldBeFalse("it waits for the agent instead of failing");

        await ServeOneAsync();

        (await queued.WithinAsync(TimeSpan.FromSeconds(5), "queued")).Result.ShouldBe("local");
        _factory.Created.ShouldBe(0);
        _redis.QueuedCount.ShouldBe(0);
    }

    [Fact]
    public async Task RedisBreakerOpen_NoLivePoller_AgentNeverComes_TimesOutAsTheSameTransientFailureAsBeforeRedis()
    {
        _health.IsAvailable = false;

        var response = await _queue.QueueAndWaitAsync(Request(pickupTimeout: TimeSpan.FromMilliseconds(300)), CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "queued");

        // Halibut's local queue answers a request nobody collected with this error; the client rebuilds it as a plain HalibutClientException, which the pipeline pauses on.
        response.Error.ShouldNotBeNull();
        response.Error.Message.ShouldContain("did not collect the request");
        response.Error.HalibutErrorType.ShouldBeNull();
    }

    private async Task ServeOneAsync()
    {
        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "dequeue");

        await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "local"), next.RequestMessage.ActivityId);
    }

    [Fact]
    public async Task RedisBreakerOpen_PollerLiveOnThisPod_LocalRequestIsNotAffected()
    {
        _health.IsAvailable = false;

        var poller = Task.Run(async () =>
        {
            var next = await _queue.DequeueAsync(CancellationToken.None);

            await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "local"), next.RequestMessage.ActivityId);
        });

        await WaitUntilAsync(() => _queue.PollerIsLive);

        var response = await _queue.QueueAndWaitAsync(Request(), CancellationToken.None).WithinAsync(TimeSpan.FromSeconds(5), "queue");
        await poller.WithinAsync(TimeSpan.FromSeconds(5), "poller");

        response.Result.ShouldBe("local");
        _factory.Created.ShouldBe(0, "with Redis down and nothing to receive from it, the Redis queue is not even created");
    }

    [Fact]
    public async Task RedisQueueCannotBeCreated_RequestWaitsLocally()
    {
        _factory.CreateFailure = new InvalidOperationException("RedisCacheConnectionString is not configured");

        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        await ServeOneAsync();

        (await queued.WithinAsync(TimeSpan.FromSeconds(5), "queued")).Result.ShouldBe("local");
        _health.Failures.ShouldBeEmpty("the breaker is closed by a ping that only exists once the queue was created, so a creation failure must not open it");
    }

    [Theory]
    [InlineData(typeof(CouldNotGetDataLossTokenInTimeHalibutClientException), true)]
    [InlineData(typeof(ErrorOccuredWhenInsertingDataIntoRedisHalibutPendingRequestQueueHalibutClientException), true)]
    [InlineData(typeof(ErrorWhilePreparingRequestForQueueHalibutClientException), false)]
    public async Task RequestNeverReachedRedis_FallsBackToTheLocalQueue_AndOnlyConnectivityFailuresCountAgainstTheBreaker(Type exceptionType, bool countsAgainstBreaker)
    {
        _redis.OnQueueAndWait = _ => throw (Exception)Activator.CreateInstance(exceptionType, "redis said no", new IOException("boom"));

        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        await ServeOneAsync();

        (await queued.WithinAsync(TimeSpan.FromSeconds(5), "queued")).Result.ShouldBe("local", $"{exceptionType.Name} before the request was queued must not fail it");
        _health.Failures.Count.ShouldBe(countsAgainstBreaker ? 1 : 0, "a message that cannot be sealed (no master key) is not Redis's fault");
    }

    // A request that was queued on Redis and then lost must surface (it may be running elsewhere): queueing it again locally could run it twice.
    [Theory]
    [InlineData(typeof(RedisDataLossHalibutClientException))]
    [InlineData(typeof(RedisQueueShutdownClientException))]
    public async Task RequestLostAfterItWasQueuedOnRedis_IsRethrownUnchanged_AsTransient(Type exceptionType)
    {
        var thrown = (Exception)Activator.CreateInstance(exceptionType, "lost");

        _redis.OnQueueAndWait = _ => throw thrown;

        var ex = await Should.ThrowAsync<Exception>(() => _queue.QueueAndWaitAsync(Request(), CancellationToken.None));

        ex.ShouldBeSameAs(thrown);
        TransientFailureClassifier.IsTransient(ex).ShouldBeTrue();
        _health.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellation_IsNotARedisFailure()
    {
        _redis.OnQueueAndWait = _ => throw new OperationCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(() => _queue.QueueAndWaitAsync(Request(), CancellationToken.None));

        _health.Failures.ShouldBeEmpty();
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
