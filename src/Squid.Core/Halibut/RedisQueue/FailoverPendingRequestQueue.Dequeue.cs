using Halibut;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;

namespace Squid.Core.Halibut.RedisQueue;

public sealed partial class FailoverPendingRequestQueue
{
    public async Task<RequestMessageWithCancellationToken> DequeueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var attached = _presence.DequeueStarted();

        if (TryTakeParked(out var parked)) return HandOut(parked.Request, parked.Origin);

        var appeared = _replicas.OtherReplicaAppeared;

        if (!_replicas.OtherReplicaIsLive && !HasRequestsOnRedis) return HandOut(await DequeueUntilAReplicaAppearsAsync(appeared, cancellationToken).ConfigureAwait(false), _local);

        if (!RedisMayDeliver()) return HandOut(await DequeueLocalOnlyAsync(cancellationToken).ConfigureAwait(false), _local);

        return await DequeueFromBothLegsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// No other replica is alive, so nothing can arrive through Redis and none is listened to (no Redis leg, no subscription): this is Halibut's own local wait, in
    /// full. It is cut short only if a replica appears, so that the next dequeue listens to Redis too; an early empty answer is an ordinary polling answer.
    /// </summary>
    private async Task<RequestMessageWithCancellationToken> DequeueUntilAReplicaAppearsAsync(CancellationToken appeared, CancellationToken ct)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct, appeared);

        try
        {
            return await _local.DequeueAsync(stopWaiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Redis cannot deliver (down, or not usable yet), so only the local queue is waited on, but not for Halibut's whole 30 second polling wait: the wait is cut to
    /// the probe interval, so that when Redis is back its requests are collected within seconds instead of after the wait. Returning early with no request is an
    /// ordinary polling answer; cancelling the local wait is safe (see <see cref="DequeueFromBothLegsAsync"/>).
    /// </summary>
    private async Task<RequestMessageWithCancellationToken> DequeueLocalOnlyAsync(CancellationToken ct)
    {
        using var recheck = CancellationTokenSource.CreateLinkedTokenSource(ct);

        recheck.CancelAfter(_options.ProbeInterval);

        try
        {
            return await _local.DequeueAsync(recheck.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits on the local queue and on the parked Redis items together. Whichever is ready first ends the wait; the local wait is then cancelled (safe: an item
    /// is taken from the local queue only after the last point at which it can observe cancellation) and its result, if it still got one, wins over a parked item,
    /// which keeps waiting for the next dequeue. An item can only ever be in one place, so none is lost or handed out twice.
    /// </summary>
    private async Task<RequestMessageWithCancellationToken> DequeueFromBothLegsAsync(CancellationToken ct)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        var local = _local.DequeueAsync(stopWaiting.Token);
        var parked = _parked.Reader.WaitToReadAsync(stopWaiting.Token).AsTask();

        await Task.WhenAny(local, parked).ConfigureAwait(false);
        await stopWaiting.CancelAsync().ConfigureAwait(false);

        var localRequest = await SettleAsync(local).ConfigureAwait(false);

        await SettleAsync(parked).ConfigureAwait(false);

        if (localRequest != null) return HandOut(localRequest, _local);

        if (TryTakeParked(out var parkedRequest)) return HandOut(parkedRequest.Request, parkedRequest.Origin);

        ct.ThrowIfCancellationRequested();

        return null;
    }

    private static async Task<T> SettleAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
    }

    private RequestMessageWithCancellationToken HandOut(RequestMessageWithCancellationToken request, IPendingRequestQueue origin)
    {
        if (request == null) return null;

        _origins[request.RequestMessage.ActivityId] = origin;
        _presence.RequestHandedOut(request.RequestMessage.ActivityId);

        return request;
    }

    /// <summary>The response goes back to the queue the request came from; an unknown id (already answered) is left to the local queue, which ignores it.</summary>
    public async Task ApplyResponse(ResponseMessage response, Guid requestActivityId)
    {
        try
        {
            if (_origins.TryRemove(requestActivityId, out var origin) && origin != _local)
                await ApplyToRedisAsync(origin, response, requestActivityId).ConfigureAwait(false);
            else
                await _local.ApplyResponse(response, requestActivityId).ConfigureAwait(false);
        }
        finally
        {
            _presence.ResponseApplied(requestActivityId);
        }
    }

    // This runs inside Halibut's polling loop, so it must neither throw into it (that would drop the agent's connection and with it every local request riding on
    // it) nor hold it: a Redis that does not answer would otherwise keep the connection waiting ~50 s per response. With the breaker open it is not attempted, and
    // otherwise it gets a budget, after which it is left to finish by itself. Either way the pod that sent the request stops hearing this pod's heartbeat and fails
    // the request itself as a transient error.
    private async Task ApplyToRedisAsync(IPendingRequestQueue redis, ResponseMessage response, Guid requestActivityId)
    {
        if (!_health.IsAvailable)
        {
            Log.Warning("Redis is down, so the response of request {RequestActivityId} for agent {Endpoint} is not handed back; the requesting pod will fail it", requestActivityId, _endpoint);

            return;
        }

        Task apply = null;

        try
        {
            apply = redis.ApplyResponse(response, requestActivityId);

            await apply.WaitAsync(_options.ApplyResponseBudget).ConfigureAwait(false);
        }
        catch (TimeoutException) when (apply is { IsCompleted: false })
        {
            _health.RecordFailure("applying a cross-pod response timed out");

            Log.Warning("Redis did not take the response of request {RequestActivityId} for agent {Endpoint} within {Budget}; the agent connection no longer waits for it", requestActivityId, _endpoint, _options.ApplyResponseBudget);

            _ = apply.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            _health.RecordFailure($"applying a cross-pod response failed: {ex.GetType().Name}");

            Log.Warning(ex, "Could not hand the response of request {RequestActivityId} for agent {Endpoint} back through Redis; the requesting pod will fail it", requestActivityId, _endpoint);
        }
    }
}
