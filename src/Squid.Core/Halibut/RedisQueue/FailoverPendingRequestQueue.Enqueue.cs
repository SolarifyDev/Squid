using Halibut.Queue.Redis.Exceptions;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;

namespace Squid.Core.Halibut.RedisQueue;

public sealed partial class FailoverPendingRequestQueue
{
    /// <summary>
    /// The routing decision: a live poller here means the in-memory queue and nothing else; otherwise the request goes through Redis to reach another pod. Whenever
    /// the Redis path cannot be used (breaker open, queue not creatable, the message cannot be sealed or sent) the request is queued locally instead, as it was
    /// before Redis was involved: it succeeds if the agent connects to this pod within the polling-queue timeout, and otherwise times out as the same transient
    /// failure as ever. Redis trouble therefore never fails a request that the local queue could have served.
    /// </summary>
    public async Task<ResponseMessage> QueueAndWaitAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        if (_presence.IsLive || !TryUseRedis(out var redis)) return await _local.QueueAndWaitAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            return await redis.QueueAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanFallBackToLocal(ex))
        {
            if (IsRedisFailure(ex)) _health.RecordFailure($"cross-pod send failed: {ex.GetType().Name}");

            _fallback.Warn(_endpoint, $"the request could not be sent through Redis ({ex.GetType().Name}: {ex.Message})");

            return await _local.QueueAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryUseRedis(out IPendingRequestQueue redis)
    {
        redis = null;

        if (!_health.IsAvailable)
        {
            _fallback.Warn(_endpoint, $"the Redis breaker is {_health.State}");

            return false;
        }

        redis = TryGetRedisQueue();

        if (redis == null) _fallback.Warn(_endpoint, "its Redis queue could not be created");

        return redis != null;
    }

    // Only failures that happen before the request is on Redis's queue: no connection or data-loss token, the write failed, or the message could not be prepared
    // (sealed with the master key, its data streams stored). Data loss and shutdown mean the request was queued and then lost: those must surface, not run twice.
    private static bool CanFallBackToLocal(Exception ex) => IsRedisFailure(ex) || ex is ErrorWhilePreparingRequestForQueueHalibutClientException;

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
