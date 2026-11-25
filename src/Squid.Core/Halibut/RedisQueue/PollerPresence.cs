using System.Collections.Concurrent;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>
/// Whether a poller of one agent is attached to THIS pod, learned only from what the queue itself sees: Halibut's polling handler keeps a dequeue outstanding
/// while the agent waits for work, and between a request being handed out and its response being applied the agent is busy on it. So a poller is live while
/// a dequeue is outstanding, or a request is in flight, or for <see cref="_window"/> after the last of those events: that tail covers the moment an agent
/// takes to reconnect, so a request issued in that gap is still routed locally. A window that is too long only means a request is routed locally to a pod the
/// agent just left (it waits there, then times out as a transient failure and is retried on a fresh decision); too short only sends a request through Redis.
/// </summary>
public sealed class PollerPresence(TimeProvider clock, TimeSpan window)
{
    private readonly TimeSpan _window = window;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly object _gate = new();
    private int _outstandingDequeues;
    private int _everSeen;
    private long _lastSignOfLife;

    public bool IsLive => Volatile.Read(ref _outstandingDequeues) > 0 || !_inFlight.IsEmpty || WithinWindow();

    public IDisposable DequeueStarted()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _outstandingDequeues);
            Touch();
        }

        return new DequeueScope(this);
    }

    /// <summary>Runs <paramref name="action"/> only if no poller is live, and keeps a poller from attaching (a dequeue from starting) until it has run, so "nobody is here" cannot go stale before it is acted on.</summary>
    public bool RunIfNotLive(Action action)
    {
        lock (_gate)
        {
            if (IsLive) return false;

            action();

            return true;
        }
    }

    public void RequestHandedOut(Guid activityId)
    {
        _inFlight[activityId] = 0;
        Touch();
    }

    public void ResponseApplied(Guid activityId)
    {
        _inFlight.TryRemove(activityId, out _);
        Touch();
    }

    private bool WithinWindow() => Volatile.Read(ref _everSeen) == 1 && clock.GetElapsedTime(Volatile.Read(ref _lastSignOfLife)) < _window;

    private void Touch()
    {
        Volatile.Write(ref _lastSignOfLife, clock.GetTimestamp());
        Volatile.Write(ref _everSeen, 1);
    }

    private void DequeueEnded()
    {
        Interlocked.Decrement(ref _outstandingDequeues);
        Touch();
    }

    private sealed class DequeueScope(PollerPresence owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.DequeueEnded();
        }
    }
}
