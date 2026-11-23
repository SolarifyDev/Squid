using Halibut.ServiceModel;

namespace Squid.Core.Halibut;

/// <summary>
/// The server's Halibut trust list as an immutable in-memory snapshot. <see cref="IsTrusted"/> is lock-free and never touches the
/// database, so a TLS handshake can never block on I/O. Writers copy, modify and swap the whole snapshot under a lock, so readers
/// always see one complete list. The database stays the source of truth: <see cref="PollingTrustDistributor"/> replaces the snapshot
/// from it (on registration, on every refresh tick, and when an unknown client connects), which is what keeps several server
/// replicas that share one database converging on the same trust list.
/// </summary>
public sealed class SnapshotTrustProvider : ITrustProvider
{
    private readonly object _writeLock = new();
    private volatile Snapshot _current = new(0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Increases by one on every swap; lets callers and tests observe that a new snapshot was published.</summary>
    public long Version => _current.Version;

    public bool IsTrusted(string clientThumbprint) => !string.IsNullOrEmpty(clientThumbprint) && _current.Thumbprints.Contains(clientThumbprint);

    public string[] ToArray() => _current.Thumbprints.ToArray();

    public void Add(string clientThumbprint)
    {
        if (string.IsNullOrEmpty(clientThumbprint)) return;

        Swap(thumbprints => thumbprints.Add(clientThumbprint));
    }

    public void Remove(string clientThumbprint)
    {
        if (string.IsNullOrEmpty(clientThumbprint)) return;

        Swap(thumbprints => thumbprints.Remove(clientThumbprint));
    }

    public void TrustOnly(IReadOnlyList<string> thumbprints)
    {
        var replacement = thumbprints.Where(t => !string.IsNullOrEmpty(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_writeLock)
            _current = new Snapshot(_current.Version + 1, replacement);
    }

    private void Swap(Action<HashSet<string>> mutate)
    {
        lock (_writeLock)
        {
            var copy = new HashSet<string>(_current.Thumbprints, StringComparer.OrdinalIgnoreCase);

            mutate(copy);

            _current = new Snapshot(_current.Version + 1, copy);
        }
    }

    // Never mutated after it is published, so concurrent readers need no lock.
    private sealed record Snapshot(long Version, HashSet<string> Thumbprints);
}
