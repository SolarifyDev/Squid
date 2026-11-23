using Halibut.Exceptions;
using Halibut.Queue.Redis.Exceptions;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;

namespace Squid.Core.Halibut.RedisQueue;

public sealed partial class FailoverPendingRequestQueue
{
    /// <summary>
    /// The routing decision: a live poller here, or no other replica to carry it, means the in-memory queue and nothing else; otherwise the request goes through
    /// Redis to reach another pod. Whenever the Redis path cannot be used (breaker open, queue not creatable, the message cannot be sealed or sent) the request is
    /// queued locally instead, as it was before Redis was involved: it succeeds if the agent connects to this pod within the polling-queue timeout, and otherwise
    /// times out as the same transient failure as ever. Redis trouble therefore never fails a request that the local queue could have served.
    /// </summary>
    public async Task<ResponseMessage> QueueAndWaitAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        if (_presence.IsLive || !TryUseRedis(out var redis)) return await QueueLocallyAsync(request, cancellationToken).ConfigureAwait(false);

        return await QueueThroughRedisAsync(redis, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResponseMessage> QueueThroughRedisAsync(IPendingRequestQueue redis, RequestMessage request, CancellationToken ct)
    {
        try
        {
            return await redis.QueueAndWaitAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanFallBackToLocal(ex))
        {
            if (IsRedisFailure(ex)) _health.RecordFailure($"cross-pod send failed: {ex.GetType().Name}");

            _fallback.Warn(_endpoint, $"the request could not be sent through Redis ({ex.GetType().Name}: {ex.Message})");

            return await _local.QueueAndWaitAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            _health.RecordFailure($"cross-pod send failed: {ex.GetType().Name}");

            Log.Warning(ex, "Request {RequestActivityId} for agent {Endpoint} may already be on Redis, so it is not queued a second time locally; it fails as a transient error", request.ActivityId, _endpoint);

            throw;
        }
    }

    /// <summary>
    /// Waits in the local queue. If the request was queued here only because a poller of the agent was attached, and that poller is then gone (the agent reconnected
    /// to another pod), the wait is abandoned and the request goes through Redis instead of sitting here until the polling-queue timeout. It is abandoned only while
    /// the request is still in the queue: one a poller took is in flight, which keeps the poller live, so it is never cancelled from under the agent.
    /// </summary>
    private async Task<ResponseMessage> QueueLocallyAsync(RequestMessage request, CancellationToken ct)
    {
        using var reroute = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopWatching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watching = WatchForRerouteAsync(reroute, stopWatching.Token);

        try
        {
            return await _local.QueueAndWaitAsync(request, reroute.Token).ConfigureAwait(false);
        }
        catch (ConnectingRequestCancelledException) when (reroute.IsCancellationRequested && !ct.IsCancellationRequested)
        {
        }
        finally
        {
            await stopWatching.CancelAsync().ConfigureAwait(false);
            await watching.ConfigureAwait(false);
        }

        Log.Information("The poller of agent {Endpoint} left this pod, so request {RequestActivityId} is sent through Redis", _endpoint, request.ActivityId);

        return await RerouteAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<ResponseMessage> RerouteAsync(RequestMessage request, CancellationToken ct)
    {
        if (!TryUseRedis(out var redis)) return await _local.QueueAndWaitAsync(request, ct).ConfigureAwait(false);

        return await QueueThroughRedisAsync(redis, request, ct).ConfigureAwait(false);
    }

    private async Task WatchForRerouteAsync(CancellationTokenSource reroute, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.ProbeInterval, _clock, stop).ConfigureAwait(false);

                if (!CanRerouteToRedis()) continue;

                await reroute.CancelAsync().ConfigureAwait(false);

                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanRerouteToRedis() => !_presence.IsLive && _replicas.OtherReplicaIsLive && _health.IsAvailable;

    private bool TryUseRedis(out IPendingRequestQueue redis)
    {
        redis = null;

        if (!_replicas.OtherReplicaIsLive) return false;

        if (!_health.IsAvailable)
        {
            _fallback.Warn(_endpoint, $"the Redis breaker is {_health.State}");

            return false;
        }

        redis = TryGetRedisQueue();

        if (redis == null) _fallback.Warn(_endpoint, "its Redis queue could not be created");

        return redis != null;
    }

    // Only failures proven to happen before the request is on Redis: no data-loss token (nothing was written), or the message could not be prepared (sealed with the
    // master key, its data streams stored). Halibut also reports a failed pulse or a lost reply as "error inserting into Redis" although the request may by then be
    // queued and collected by another pod, and data loss and shutdown mean it was queued and then lost: those must surface, not run twice.
    private static bool CanFallBackToLocal(Exception ex) => ex is CouldNotGetDataLossTokenInTimeHalibutClientException or ErrorWhilePreparingRequestForQueueHalibutClientException;

    // Only the two failures that mean "could not reach or write to Redis" count against the breaker; a message that cannot be sealed is not Redis's fault.
    private static bool IsRedisFailure(Exception ex) => ex is CouldNotGetDataLossTokenInTimeHalibutClientException or ErrorOccuredWhenInsertingDataIntoRedisHalibutPendingRequestQueueHalibutClientException;

    private IPendingRequestQueue TryGetRedisQueue()
    {
        lock (_gate)
        {
            if (_disposed) return null;

            return _redis ?? TryCreateRedisQueueLocked();
        }
    }
}
