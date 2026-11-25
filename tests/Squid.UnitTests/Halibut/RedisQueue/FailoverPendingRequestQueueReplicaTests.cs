using System.Linq;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;
using Shouldly;
using Squid.Core.Halibut.RedisQueue;
using Xunit;
using static Squid.UnitTests.Halibut.RedisQueue.RedisQueueTestSupport;

namespace Squid.UnitTests.Halibut.RedisQueue;

/// <summary>
/// Redis exists to carry a request from one server replica to another. With no other live replica the queue must be entirely in memory and must not touch Redis at all,
/// and when the poller of an agent leaves a pod for another, a request waiting for it must follow it.
/// </summary>
public sealed class FailoverPendingRequestQueueReplicaTests : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly PendingRequestQueueAsync _local = LocalQueue(TimeSpan.FromSeconds(30));
    private readonly FakeRedisQueue _redis = new();
    private readonly FakeRedisFactory _factory;
    private readonly FakeHealth _health = new();
    private readonly ReplicaPresence _replicas = new();
    private readonly FailoverPendingRequestQueue _queue;

    public FailoverPendingRequestQueueReplicaTests()
    {
        _factory = new FakeRedisFactory(_redis);
        _queue = NewQueue(_local, Options() with { ProbeInterval = TimeSpan.FromMilliseconds(100), ApplyResponseBudget = TimeSpan.FromMilliseconds(300) });
    }

    public async ValueTask DisposeAsync() => await _queue.DisposeAsync();

    private FailoverPendingRequestQueue NewQueue(IPendingRequestQueue local, RedisQueueOptions options) => new(Endpoint, local, _factory, new FailoverQueueContext(_health, options, TimeProvider.System, null, _replicas));

    [Fact]
    public async Task NoOtherReplica_RequestWithNoPoller_WaitsLocally_AndRedisIsNeverTouched()
    {
        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        await Task.Delay(300);
        queued.IsCompleted.ShouldBeFalse("it waits in memory for the agent, as it did before Redis was involved");

        await ServeOneAsync();

        (await queued.WithinAsync(Limit, "queued")).Result.ShouldBe("local");
        _factory.Created.ShouldBe(0, "not even the Redis queue is created");
        _redis.QueuedCount.ShouldBe(0);
        _redis.DequeueCalls.ShouldBe(0);
    }

    [Fact]
    public async Task NoOtherReplica_ADequeueRunsNoRedisLeg_AndWaitsTheWholeLocalWait()
    {
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await Task.Delay(600);

        polling.IsCompleted.ShouldBeFalse("a single replica keeps Halibut's own polling wait; it is not cut short to listen for Redis");
        _factory.Created.ShouldBe(0);
        _redis.DequeueCalls.ShouldBe(0, "no Redis leg, no subscription");

        var request = Request();
        var queued = _queue.QueueAndWaitAsync(request, CancellationToken.None);

        (await polling.WithinAsync(Limit, "dequeue")).RequestMessage.Id.ShouldBe(request.Id);

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);
        await queued.WithinAsync(Limit, "queued");
    }

    [Fact]
    public async Task AReplicaAppearsWhileADequeueWaits_ItReturnsEmpty_AndTheNextOneListensToRedis()
    {
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await Task.Delay(200);
        _redis.DequeueCalls.ShouldBe(0);

        _replicas.Set(true);

        (await polling.WithinAsync(Limit, "dequeue")).ShouldBeNull("an ordinary empty polling answer; the agent asks again at once");

        var next = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var crossPod = Request();

        await _redis.Items.Writer.WriteAsync(Dequeued(crossPod));

        (await next.WithinAsync(Limit, "dequeue")).RequestMessage.Id.ShouldBe(crossPod.Id);
    }

    [Fact]
    public async Task TheOtherReplicaGoesAway_RequestsAreLocalAgain()
    {
        _replicas.Set(true);
        _replicas.Set(false);

        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        await ServeOneAsync();
        await queued.WithinAsync(Limit, "queued");

        _redis.QueuedCount.ShouldBe(0);
        _factory.Created.ShouldBe(0);
    }

    [Fact]
    public async Task ADequeueStartedWhileAReplicaIsLive_StillListensToRedis()
    {
        _replicas.Set(true);

        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);

        var crossPod = Request();

        await _redis.Items.Writer.WriteAsync(Dequeued(crossPod));

        (await polling.WithinAsync(Limit, "dequeue")).RequestMessage.Id.ShouldBe(crossPod.Id);
    }

    // The agent moved to another pod: this pod's last dequeue is over, nothing polls here any more, and the request that was queued while the poller still counted as
    // attached would otherwise wait for the whole polling-queue timeout, unseen by the pod that now holds the agent.
    [Fact]
    public async Task RequestQueuedWhileThePollerCounted_ButThePollerThenLeftForAnotherPod_FollowsItThroughRedis()
    {
        _replicas.Set(true);

        await PollerLeavesAsync();

        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        _queue.PollerIsLive.ShouldBeTrue("queued inside the liveness window, so it is queued locally first");

        var response = await queued.WithinAsync(Limit, "the request must follow the agent");

        response.Result.ShouldBe("redis");
        _redis.QueuedCount.ShouldBe(1);
    }

    // The other replica was killed hard: it still looked alive when this request was queued, so it went through Redis; the agent then reconnected HERE and this pod is
    // alone. Redis is the only place the request is, so this pod's own dequeue must still collect it, long before the pickup timeout.
    [Fact]
    public async Task RequestQueuedThroughRedis_ThenTheOtherReplicaExpires_TheLocalPollerCollectsItFromRedis()
    {
        _replicas.Set(true);

        var request = Request();
        var answered = new TaskCompletionSource<ResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _redis.OnQueueAndWait = _ => answered.Task;

        var queued = _queue.QueueAndWaitAsync(request, CancellationToken.None);

        await WaitUntilAsync(() => _redis.QueuedCount == 1);

        _replicas.Set(false);

        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls >= 1);
        await _redis.Items.Writer.WriteAsync(Dequeued(request));

        var handedOut = await polling.WithinAsync(Limit, "dequeue");

        handedOut.RequestMessage.Id.ShouldBe(request.Id);

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "agent"), request.ActivityId);

        answered.SetResult(ResponseMessage.FromResult(request, "redis"));
        (await queued.WithinAsync(Limit, "queued")).Result.ShouldBe("redis");
        _redis.Applied.Single().ActivityId.ShouldBe(request.ActivityId, "the answer goes back to the queue the request came from");
    }

    // The routing decision chose Redis while the other replica looked alive; before the request is counted as on Redis the replica expires and the agent's dequeue
    // starts: it sees a lone pod with nothing on Redis and waits on memory only. The request this pod then puts on Redis must wake that dequeue, or it would sit there
    // for the whole polling wait while the dequeue that could collect it is not listening.
    [Fact]
    public async Task ARequestQueuedOnRedis_WakesADequeueThatStartedLoneJustBefore_SoTheNextOneCollectsIt()
    {
        _replicas.Set(true);

        var request = Request();
        var answered = new TaskCompletionSource<ResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RequestMessageWithCancellationToken> lone = null;

        _redis.OnQueueAndWait = _ => answered.Task;
        _factory.OnCreate = () =>
        {
            _replicas.Set(false);
            lone = _queue.DequeueAsync(CancellationToken.None);
        };

        var queued = _queue.QueueAndWaitAsync(request, CancellationToken.None);

        await WaitUntilAsync(() => _redis.QueuedCount == 1);

        (await lone.WithinAsync(Limit, "the lone dequeue")).ShouldBeNull("an ordinary empty polling answer, given as soon as the request is on Redis");

        var next = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls >= 1);
        await _redis.Items.Writer.WriteAsync(Dequeued(request));

        (await next.WithinAsync(Limit, "the next dequeue")).RequestMessage.Id.ShouldBe(request.Id);

        answered.SetResult(ResponseMessage.FromResult(request, "redis"));
        await queued.WithinAsync(Limit, "queued");
    }

    [Fact]
    public async Task OnceTheRedisRequestIsAnswered_AloneAgain_TheDequeueDoesNotListenToRedis()
    {
        _replicas.Set(true);

        await _queue.QueueAndWaitAsync(Request(), CancellationToken.None).WithinAsync(Limit, "queued");

        _replicas.Set(false);

        var polling = _queue.DequeueAsync(CancellationToken.None);

        await Task.Delay(300);

        polling.IsCompleted.ShouldBeFalse();
        _redis.DequeueCalls.ShouldBe(0, "nothing of this pod is on Redis any more, so a single replica listens to memory only");
    }

    [Fact]
    public async Task RedisRequestCancelledByItsCaller_AloneAgain_TheDequeueDoesNotListenToRedis()
    {
        _replicas.Set(true);

        _redis.OnQueueAndWait = _ => Task.Delay(Timeout.Infinite).ContinueWith(_ => (ResponseMessage)null);

        using var cancel = new CancellationTokenSource();
        var queued = _queue.QueueAndWaitAsync(Request(), cancel.Token);

        await WaitUntilAsync(() => _redis.QueuedCount == 1);

        _replicas.Set(false);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        var polling = _queue.DequeueAsync(CancellationToken.None);

        await Task.Delay(300);

        _redis.DequeueCalls.ShouldBe(0, "the cancelled request is no longer on Redis for this pod");
        polling.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task AttachedPoller_RunIfNotLiveDoesNotRun()
    {
        var presence = new PollerPresence(TimeProvider.System, TimeSpan.FromMilliseconds(1));
        var ran = false;

        using var attached = presence.DequeueStarted();

        presence.RunIfNotLive(() => ran = true).ShouldBeFalse();
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task NoPoller_RunIfNotLiveRuns()
    {
        var presence = new PollerPresence(TimeProvider.System, TimeSpan.FromMilliseconds(1));
        var ran = false;

        presence.RunIfNotLive(() => ran = true).ShouldBeTrue();
        ran.ShouldBeTrue();
    }

    // The seam: the reroute must call the atomic check-and-cancel. While the cancellation is being delivered (inside the call), a poller that starts to attach must be
    // held off, so the poller can never take a request that is cancelled in the same instant. The attach is made from inside the cancel callback and is observed
    // deterministically: it either becomes live (check and cancel were split) or its thread blocks on the presence gate (they are one step).
    [Fact]
    public async Task WhileTheRerouteCancels_APollerCannotAttachUntilItIsDone()
    {
        _replicas.Set(true);

        var local = new ProbingLocalQueue(LocalQueue(TimeSpan.FromSeconds(30)));
        await using var queue = NewQueue(local, Options(TimeSpan.FromMilliseconds(400)) with { ProbeInterval = TimeSpan.FromMilliseconds(3) });

        local.Queue = queue;

        await PollerLeavesAsync(queue);

        var response = await queue.QueueAndWaitAsync(Request(), CancellationToken.None).WithinAsync(Limit, "rerouted");

        response.Result.ShouldBe("redis");
        local.AttachOutcome.ShouldBe(AttachOutcome.HeldOff, "the poller was held off while the cancellation was delivered");
    }

    private enum AttachOutcome
    {
        Unobserved,
        HeldOff,
        AttachedDuringCancel
    }

    private sealed class ProbingLocalQueue(PendingRequestQueueAsync inner) : IPendingRequestQueue
    {
        private readonly CancellationTokenSource _pollerStops = new();

        public FailoverPendingRequestQueue Queue { get; set; }

        public AttachOutcome AttachOutcome { get; private set; }

        public bool IsEmpty => inner.IsEmpty;

        public int Count => inner.Count;

        public Task<ResponseMessage> QueueAndWaitAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.Register(ObserveAnAttachDuringTheCancel);

            return inner.QueueAndWaitAsync(request, cancellationToken);
        }

        private void ObserveAnAttachDuringTheCancel()
        {
            if (AttachOutcome != AttachOutcome.Unobserved) return;

            var attaching = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() => { attaching.SetResult(Thread.CurrentThread); return Queue.DequeueAsync(_pollerStops.Token); });

            var thread = attaching.Task.GetAwaiter().GetResult();
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (!Queue.PollerIsLive && (thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("the attaching poller neither attached nor blocked");

                Thread.Yield();
            }

            AttachOutcome = Queue.PollerIsLive ? AttachOutcome.AttachedDuringCancel : AttachOutcome.HeldOff;
        }

        public Task<RequestMessageWithCancellationToken> DequeueAsync(CancellationToken cancellationToken) => inner.DequeueAsync(cancellationToken);

        public Task ApplyResponse(ResponseMessage response, Guid requestActivityId) => inner.ApplyResponse(response, requestActivityId);

        public ValueTask DisposeAsync()
        {
            _pollerStops.Cancel();

            return inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequestAPollerAlreadyTook_IsNeverRerouted_HoweverLongThePollerTakes()
    {
        _replicas.Set(true);

        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _queue.PollerIsLive);

        var request = Request();
        var queued = _queue.QueueAndWaitAsync(request, CancellationToken.None);

        (await polling.WithinAsync(Limit, "dequeue")).RequestMessage.Id.ShouldBe(request.Id);

        await Task.Delay(1000);
        queued.IsCompleted.ShouldBeFalse();

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "from the agent"), request.ActivityId);

        (await queued.WithinAsync(Limit, "queued")).Result.ShouldBe("from the agent");
        _redis.QueuedCount.ShouldBe(0, "a request that is in flight on a live poller is never taken away from it");
    }

    [Fact]
    public async Task NoOtherReplica_RequestIsNeverRerouted()
    {
        await PollerLeavesAsync();

        var queued = _queue.QueueAndWaitAsync(Request(), CancellationToken.None);

        await Task.Delay(1200);

        queued.IsCompleted.ShouldBeFalse();
        _redis.QueuedCount.ShouldBe(0);

        await ServeOneAsync();
        await queued.WithinAsync(Limit, "queued");
    }

    [Fact]
    public async Task CallerCancelsARequestThatWasWaitingLocally_ItIsACancellationNotAReroute()
    {
        _replicas.Set(true);

        await PollerLeavesAsync();

        using var cancel = new CancellationTokenSource();
        var queued = _queue.QueueAndWaitAsync(Request(pickupTimeout: TimeSpan.FromSeconds(30)), cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        _redis.QueuedCount.ShouldBe(0);
    }

    // Halibut keeps the dequeue of a vanished agent outstanding for its polling wait, so a response written to a dead Redis would hold the agent's connection for ~50 s.
    [Fact]
    public async Task ResponseToACrossPodRequest_StuckInRedis_ReleasesTheAgentConnectionWithinTheBudget_AndCountsAgainstTheBreaker()
    {
        var request = await HandOutCrossPodAsync();

        _redis.Hang = true;

        // The fake never answers and the budget is 300 ms: that the call returns at all is the proof it stopped waiting.
        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId).WithinAsync(Limit, "apply");

        _health.Failures.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResponseToACrossPodRequest_WithTheBreakerOpen_IsNotAttemptedAtAll()
    {
        var request = await HandOutCrossPodAsync();

        _health.IsAvailable = false;

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId).WithinAsync(TimeSpan.FromSeconds(2), "apply");

        _redis.Applied.ShouldBeEmpty("Redis is known to be down: the write is not even tried, the requesting pod fails the request itself");
    }

    [Fact]
    public async Task ResponseToACrossPodRequest_WithinTheBudget_IsApplied()
    {
        var request = await HandOutCrossPodAsync();

        await _queue.ApplyResponse(ResponseMessage.FromResult(request, "ok"), request.ActivityId);

        _redis.Applied.Single().ActivityId.ShouldBe(request.ActivityId);
        _health.Failures.ShouldBeEmpty();
    }

    private async Task<RequestMessage> HandOutCrossPodAsync()
    {
        _replicas.Set(true);

        var request = Request();
        var polling = _queue.DequeueAsync(CancellationToken.None);

        await WaitUntilAsync(() => _redis.DequeueCalls == 1);
        await _redis.Items.Writer.WriteAsync(Dequeued(request));
        await polling.WithinAsync(Limit, "dequeue");

        return request;
    }

    /// <summary>A dequeue that started and ended: the poller was here a moment ago and is now gone, but still counts as attached for the liveness window.</summary>
    private Task PollerLeavesAsync() => PollerLeavesAsync(_queue);

    private static async Task PollerLeavesAsync(FailoverPendingRequestQueue queue)
    {
        using var attached = new CancellationTokenSource();
        var poll = queue.DequeueAsync(attached.Token);

        await WaitUntilAsync(() => queue.PollerIsLive);
        await attached.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
    }

    private async Task ServeOneAsync()
    {
        var next = await _queue.DequeueAsync(CancellationToken.None).WithinAsync(Limit, "dequeue");

        await _queue.ApplyResponse(ResponseMessage.FromResult(next.RequestMessage, "local"), next.RequestMessage.ActivityId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never became true");

            await Task.Delay(10);
        }
    }
}
