namespace Squid.Core.Halibut.RedisQueue;

/// <summary>
/// Whether another server replica is alive. Redis only ever carries a request between replicas, so with no other live replica there is nothing for it to do and the
/// queue stays entirely in memory: a single-replica install never touches Redis in the request path, and Redis trouble cannot reach it.
/// </summary>
public interface IReplicaPresence
{
    bool OtherReplicaIsLive { get; }

    /// <summary>Cancelled when another replica appears while none was live. Read it before <see cref="OtherReplicaIsLive"/>, so an appearance between the two reads is not missed.</summary>
    CancellationToken OtherReplicaAppeared { get; }
}

/// <summary>The current answer, written by whatever observes the replicas (<see cref="ReplicaHeartbeat"/>) and read by every queue.</summary>
public sealed class ReplicaPresence : IReplicaPresence
{
    private readonly object _gate = new();
    private CancellationTokenSource _appeared = new();
    private volatile bool _otherLive;

    public bool OtherReplicaIsLive => _otherLive;

    public CancellationToken OtherReplicaAppeared
    {
        get
        {
            lock (_gate) return _appeared.Token;
        }
    }

    public void Set(bool otherReplicaIsLive)
    {
        CancellationTokenSource toSignal = null;

        lock (_gate)
        {
            if (_otherLive == otherReplicaIsLive) return;

            _otherLive = otherReplicaIsLive;

            if (otherReplicaIsLive) toSignal = _appeared;
            else _appeared = new CancellationTokenSource();
        }

        toSignal?.Cancel();
    }
}
