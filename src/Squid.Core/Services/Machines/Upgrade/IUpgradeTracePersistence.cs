using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// DB-backed durable persistence for an <see cref="UpgradeTraceSnapshot"/>.
/// Composed with the in-memory <see cref="IUpgradeEventTimelineStore"/> so that
/// when an upgrade FIRST reaches a terminal status, its snapshot is written
/// through to the <c>machine.last_upgrade_trace_json</c> column, and the
/// <see cref="UpgradeTraceHydrator"/> re-populates the in-memory store from
/// these rows at server startup.
///
/// <para><b>Why this exists</b>: the upgrade timeline cache is intentionally
/// in-memory (writing it on every Capabilities probe would dominate the cost of
/// the upgrade). But that means a server pod restart erased an operator's view
/// of how the most recent upgrade concluded. This persistence is the durable
/// backstop — written ONCE per upgrade (gated by
/// <see cref="IUpgradeTracePersistenceGate"/> so a terminal status the agent
/// keeps re-reporting on every probe is persisted only the first time).</para>
///
/// <para><b>Stability</b>: this interface is internal to the upgrade subsystem.
/// Tests use a fake implementation. Production wires
/// <see cref="UpgradeTracePersistence"/> via <see cref="IScopedDependency"/>.</para>
/// </summary>
public interface IUpgradeTracePersistence
{
    /// <summary>
    /// Persist <paramref name="snapshot"/> for <paramref name="machineId"/> to
    /// <c>machine.last_upgrade_trace_json</c> + set
    /// <c>last_upgrade_trace_updated_at</c>. Atomic single-row update via
    /// <see cref="IRepository.ExecuteUpdateAsync{T}"/> — no entity tracking, no
    /// concurrent-write race with concurrent <c>UpdateMachineAsync</c> calls.
    /// </summary>
    Task SaveAsync(int machineId, UpgradeTraceSnapshot snapshot, CancellationToken ct);

    /// <summary>
    /// Load every machine row with a non-NULL <c>last_upgrade_trace_json</c> for
    /// the hydrator. Skips rows where deserialisation fails (logged warning) so
    /// one corrupt JSON blob can't block startup.
    /// </summary>
    Task<IReadOnlyList<(int MachineId, UpgradeTraceSnapshot Snapshot)>> LoadAllAsync(CancellationToken ct);
}

public sealed class UpgradeTracePersistence : IUpgradeTracePersistence, IScopedDependency
{
    /// <summary>
    /// Stable JSON serialisation contract. The wrapper keys are written via
    /// explicit <c>[JsonPropertyName]</c> on <see cref="UpgradeTraceSnapshot"/>
    /// and the nested <see cref="UpgradeStatusPayload"/> / <see cref="UpgradeEvent"/>;
    /// camelCase is set anyway to match the existing persistence convention and
    /// guard against a future plain-POCO field slipping in without an attribute.
    /// Pinned by <c>UpgradeTracePersistenceShapeTests</c> so a future
    /// <see cref="JsonSerializerOptions"/> default change doesn't silently break
    /// the hydration round-trip on operators' existing rows.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IRepository _repository;

    public UpgradeTracePersistence(IRepository repository)
    {
        _repository = repository;
    }

    public async Task SaveAsync(int machineId, UpgradeTraceSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);

        var now = DateTimeOffset.UtcNow;

        await _repository.ExecuteUpdateAsync<Machine>(
            m => m.Id == machineId,
            s => s.SetProperty(m => m.LastUpgradeTraceJson, json)
                  .SetProperty(m => m.LastUpgradeTraceUpdatedAt, (DateTimeOffset?)now),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(int MachineId, UpgradeTraceSnapshot Snapshot)>> LoadAllAsync(CancellationToken ct)
    {
        // Project to (id, json) — avoid full entity hydration just for the trace
        // blob on startup. Skip rows with NULL json (machines that never had a
        // terminal upgrade observed).
        var rows = await _repository.QueryNoTracking<Machine>(m => m.LastUpgradeTraceJson != null)
            .Select(m => new { m.Id, m.LastUpgradeTraceJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var results = new List<(int, UpgradeTraceSnapshot)>(rows.Count);

        foreach (var row in rows)
        {
            var snapshot = TryDeserialize(row.Id, row.LastUpgradeTraceJson);

            if (snapshot != null) results.Add((row.Id, snapshot));
        }

        return results;
    }

    private static UpgradeTraceSnapshot TryDeserialize(int machineId, string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpgradeTraceSnapshot>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // One corrupted row can't block startup. Log + skip so the next
            // terminal upgrade overwrites the bad blob without needing a
            // server-image rebuild.
            Log.Warning(ex,
                "Failed to deserialise persisted upgrade trace for machine {MachineId} — skipping. " +
                "The cache will repopulate from the next terminal upgrade for this machine.",
                machineId);

            return null;
        }
    }
}
