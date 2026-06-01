namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Orchestrates the durable upgrade-trace backstop: when a machine's in-memory
/// timeline reflects a TERMINAL upgrade outcome, snapshot it (status + events +
/// Phase B log) and persist it to the DB — exactly once per concluded upgrade.
///
/// <para>This is the single home for the "should we durably persist this trace,
/// and if so do it" decision, keeping that concern out of the health-check
/// strategy (whose job is probing the agent, not trace durability). It composes
/// three collaborators: the in-memory <see cref="IUpgradeEventTimelineStore"/>
/// (source of the current snapshot), the <see cref="IUpgradeTracePersistence"/>
/// (the DB write), and the <see cref="IUpgradeTracePersistenceGate"/> (dedup so
/// the same terminal status the agent re-reports on every probe is written
/// once, not per-probe).</para>
/// </summary>
public interface IUpgradeTracePersister
{
    /// <summary>
    /// If the machine's current in-memory status is terminal AND not already
    /// persisted, write the trace snapshot to the DB and mark the dedup gate.
    /// No-op when the status is absent / in-flight / already persisted. Never
    /// throws — a DB failure is logged and the gate left open to retry on the
    /// next probe.
    /// </summary>
    Task PersistIfTerminalAsync(int machineId, CancellationToken ct);
}

public sealed class UpgradeTracePersister : IUpgradeTracePersister, IScopedDependency
{
    private readonly IUpgradeEventTimelineStore _store;
    private readonly IUpgradeTracePersistence _persistence;
    private readonly IUpgradeTracePersistenceGate _gate;

    public UpgradeTracePersister(IUpgradeEventTimelineStore store, IUpgradeTracePersistence persistence, IUpgradeTracePersistenceGate gate)
    {
        _store = store;
        _persistence = persistence;
        _gate = gate;
    }

    public async Task PersistIfTerminalAsync(int machineId, CancellationToken ct)
    {
        var status = _store.GetStatus(machineId);

        if (status == null || !UpgradeStatusClassifier.IsTerminal(status.Status)) return;

        var snapshot = new UpgradeTraceSnapshot
        {
            Status = status,
            Events = _store.Get(machineId),
            Log = _store.GetLog(machineId)
        };

        if (_gate.AlreadyPersisted(machineId, snapshot.Signature)) return;

        try
        {
            await _persistence.SaveAsync(machineId, snapshot, ct).ConfigureAwait(false);

            _gate.MarkPersisted(machineId, snapshot.Signature);

            Log.Information("[UpgradeAudit] Persisted terminal upgrade trace for machine {MachineId} (status {Status}) — survives server pod restart.", machineId, status.Status);
        }
        catch (Exception ex)
        {
            // Best-effort: a DB hiccup must not fail the health check. The
            // in-memory store still holds the trace; the gate is left open so
            // the next probe retries the write.
            Log.Warning(ex, "[UpgradeAudit] Failed to persist terminal upgrade trace for machine {MachineId} — in-memory cache still holds it; next health-check probe will retry the DB write.", machineId);
        }
    }
}
