using System.Collections.Concurrent;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Process-local dedup gate that lets the durable upgrade-trace persister write
/// a terminal outcome <b>exactly once</b> per concluded upgrade, instead of on
/// every Capabilities probe.
///
/// <para><b>Why it's needed</b>: once an upgrade concludes, the agent keeps
/// reporting the SAME terminal status (e.g. <c>SUCCESS</c>) on every subsequent
/// probe — its <c>last-upgrade.json</c> isn't rewritten until the next upgrade.
/// Persisting on every probe-with-a-terminal-status would re-introduce the
/// per-probe DB-write cost the in-memory store was designed to avoid. This gate
/// records the last <see cref="UpgradeTraceSnapshot.Signature"/> persisted per
/// machine and reports whether the current one is new.</para>
///
/// <para><b>Lifetime</b>: singleton. The map is rebuilt on restart; the
/// <see cref="UpgradeTraceHydrator"/> primes it from the DB at startup so the
/// first post-restart probe doesn't re-persist a snapshot already on disk.
/// A benign double-write under concurrent probes for the same machine is
/// harmless (the DB write is idempotent — same signature ⇒ same bytes).</para>
/// </summary>
public interface IUpgradeTracePersistenceGate
{
    /// <summary>
    /// True when a snapshot with <paramref name="signature"/> has already been
    /// persisted for <paramref name="machineId"/> (so the caller should skip the
    /// DB write). A different signature — i.e. a NEW terminal outcome — returns
    /// false.
    /// </summary>
    bool AlreadyPersisted(int machineId, string signature);

    /// <summary>
    /// Record <paramref name="signature"/> as the last-persisted snapshot for
    /// <paramref name="machineId"/>. Called only AFTER a successful DB write, so
    /// a failed write leaves the gate open for the next probe to retry.
    /// </summary>
    void MarkPersisted(int machineId, string signature);
}

public sealed class UpgradeTracePersistenceGate : IUpgradeTracePersistenceGate, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, string> _lastPersistedSignature = new();

    public bool AlreadyPersisted(int machineId, string signature)
    {
        return _lastPersistedSignature.TryGetValue(machineId, out var existing)
               && string.Equals(existing, signature, StringComparison.Ordinal);
    }

    public void MarkPersisted(int machineId, string signature)
    {
        _lastPersistedSignature[machineId] = signature ?? string.Empty;
    }
}
