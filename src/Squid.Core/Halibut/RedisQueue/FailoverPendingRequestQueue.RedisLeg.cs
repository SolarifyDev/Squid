using Halibut;
using Halibut;
using Halibut.ServiceModel;
using Halibut.Transport.Protocol;

namespace Squid.Core.Halibut.RedisQueue;

public sealed partial class FailoverPendingRequestQueue
{
    /// <summary>A request taken from Redis that no dequeue was waiting for. Exactly one of the dequeue that reads it and the expiry that gives up on it claims it.</summary>
    private sealed class ParkedRequest(RequestMessageWithCancellationToken request, IPendingRequestQueue origin)
    {
        private int _claimed;

        public RequestMessageWithCancellationToken Request { get; } = request;

        public IPendingRequestQueue Origin { get; } = origin;

        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }

    /// <summary>
    /// True when a dequeue should also listen for Redis items. A Redis leg is started only here, only while the breaker is closed and only by a dequeue that
    /// is about to wait, so an agent that is busy (no dequeue outstanding) never has a Redis item pulled out from under it, and a Redis that is down is never
    /// waited on.
    /// </summary>
    private bool RedisMayDeliver()
    {
        lock (_gate)
        {
            if (_disposed) return false;

            if (_redisLeg is { IsCompleted: false } || _parked.Reader.Count > 0) return true;

            if (!_health.IsAvailable) return false;

            var redis = _redis ?? TryCreateRedisQueueLocked();

            if (redis == null) return false;

            _redisLeg = Task.Run(() => RunRedisLegAsync(redis));

            return true;
        }
    }

    private IPendingRequestQueue TryCreateRedisQueueLocked()
    {
        try
        {
            return _redis = _redisFactory.CreateQueue(_endpoint);
        }
        catch (Exception ex)
        {
            // Not a breaker failure: the breaker is recovered by the ping, which only exists once the queue could be created, so it could never close again.
            Log.Warning(ex, "Could not create the Redis queue for agent {Endpoint}", _endpoint);

            return null;
        }
    }

    /// <summary>One Redis dequeue (it ends by itself after Halibut's polling wait). Its outcome never reaches the caller of a local dequeue: an item is parked, an error is the breaker's business.</summary>
    private async Task RunRedisLegAsync(IPendingRequestQueue redis)
    {
        try
        {
            var request = await redis.DequeueAsync(_lifetime.Token).ConfigureAwait(false);

            if (request != null) Park(new ParkedRequest(request, redis));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The Redis leg of the queue for agent {Endpoint} failed", _endpoint);

            if (!IsUnreadableMessage(ex)) _health.RecordFailure($"dequeue failed: {ex.GetType().Name}");
        }
    }

    // A message that cannot be opened (tampered, sealed under another master key) is a problem with that message, not with Redis.
    private static bool IsUnreadableMessage(Exception ex) => ex is Encryption.QueueMessageUnreadableException || ex.InnerException is Encryption.QueueMessageUnreadableException;

    private void Park(ParkedRequest parked)
    {
        _parked.Writer.TryWrite(parked);

        _ = Task.Run(() => FailIfNobodyComesAsync(parked));
    }

    /// <summary>
    /// A parked item waits for the agent's next dequeue, however long the agent is busy on another request. It is given up on only when the poller is gone
    /// (not live for a whole window), because then nobody will ever collect it and the pod that sent it would wait for the full pickup timeout.
    /// </summary>
    private async Task FailIfNobodyComesAsync(ParkedRequest parked)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.LivenessWindow, _clock, _lifetime.Token).ConfigureAwait(false);

                if (_presence.IsLive) continue;

                if (parked.TryClaim()) await FailAsync(parked, "no poller of the agent is attached to the pod that took the request").ConfigureAwait(false);

                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool TryTakeParked(out ParkedRequest parked)
    {
        while (_parked.Reader.TryRead(out parked))
            if (parked.TryClaim()) return true;

        parked = null;

        return false;
    }

    private async Task FailParkedRequestsAsync(string reason)
    {
        while (_parked.Reader.TryRead(out var parked))
            if (parked.TryClaim()) await FailAsync(parked, reason).ConfigureAwait(false);
    }

    private async Task FailAsync(ParkedRequest parked, string reason)
    {
        var request = parked.Request.RequestMessage;

        try
        {
            await parked.Origin.ApplyResponse(ResponseMessage.FromException(request, new HalibutClientException($"The request could not be delivered: {reason}.")), request.ActivityId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not fail the undelivered request {RequestActivityId} for agent {Endpoint}", request.ActivityId, _endpoint);
        }
    }
}
