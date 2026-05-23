using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// H2 — DB-backed persistence for <see cref="MachineRuntimeCapabilities"/>.
/// Composed with <see cref="InMemoryMachineRuntimeCapabilitiesCache"/> so that
/// every successful capabilities probe gets written through to the
/// <c>machine.runtime_capabilities_json</c> column AND the
/// <see cref="MachineRuntimeCapabilitiesCacheHydrator"/> can re-populate the
/// in-memory cache at server startup from the DB rows.
///
/// <para><b>Why this exists</b>: in 1.7.x the in-memory cache was wiped on
/// every server pod restart. The first operator action after a deploy
/// (typically the most stressful moment) hit the H1 NoOsDetected path until
/// the next scheduled health check repopulated the cache. Now the DB is the
/// source of truth — cache misses fall through to DB; the in-memory dict is
/// just a hot-read accelerator.</para>
///
/// <para><b>Stability</b>: this interface is internal to the deployment
/// execution layer. Tests use a fake implementation. Production wires
/// <see cref="MachineRuntimeCapabilitiesPersistence"/> via
/// <see cref="IScopedDependency"/>.</para>
/// </summary>
public interface IMachineRuntimeCapabilitiesPersistence
{
    /// <summary>
    /// Persist <paramref name="capabilities"/> for <paramref name="machineId"/>
    /// to <c>machine.runtime_capabilities_json</c> + sets the
    /// <c>runtime_capabilities_updated_at</c> timestamp. Atomic single-row
    /// update via <see cref="IRepository.ExecuteUpdateAsync{T}"/> — no entity
    /// tracking, no concurrent-write race with concurrent <c>UpdateMachineAsync</c>
    /// calls.
    /// </summary>
    Task SaveAsync(int machineId, MachineRuntimeCapabilities capabilities, CancellationToken ct);

    /// <summary>
    /// Load every machine row with non-NULL <c>runtime_capabilities_json</c> for
    /// the hydrator. Skips rows where deserialisation fails (logged warning) so
    /// one corrupt JSON blob can't block startup.
    /// </summary>
    Task<IReadOnlyList<(int MachineId, MachineRuntimeCapabilities Capabilities)>> LoadAllAsync(CancellationToken ct);

    /// <summary>
    /// NULL out both columns for <paramref name="machineId"/>. Called after a
    /// successful upgrade so the next health check repopulates with the new
    /// agent version — matches the in-memory cache <c>Invalidate</c> contract.
    /// </summary>
    Task InvalidateAsync(int machineId, CancellationToken ct);
}

public sealed class MachineRuntimeCapabilitiesPersistence : IMachineRuntimeCapabilitiesPersistence, IScopedDependency
{
    private readonly IRepository _repository;

    /// <summary>
    /// Stable JSON serialisation contract — property names are written
    /// camelCase to match the existing API conventions, no enum-as-string
    /// magic (the underlying type doesn't have enums anyway). Pinned by
    /// <c>MachineRuntimeCapabilitiesPersistence_SerialisedJsonShape_Stable</c>
    /// unit test so a future <see cref="JsonSerializerOptions"/> default change
    /// (e.g. the .NET 10 PascalCase regression) doesn't silently break the
    /// hydration round-trip.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public MachineRuntimeCapabilitiesPersistence(IRepository repository)
    {
        _repository = repository;
    }

    public async Task SaveAsync(int machineId, MachineRuntimeCapabilities capabilities, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var json = JsonSerializer.Serialize(new PersistedCapabilities
        {
            Os = capabilities.Os,
            OsVersion = capabilities.OsVersion,
            DefaultShell = capabilities.DefaultShell,
            InstalledShells = capabilities.InstalledShells,
            Architecture = capabilities.Architecture,
            AgentVersion = capabilities.AgentVersion,
            SupportedServices = capabilities.SupportedServices?.ToArray() ?? Array.Empty<string>(),
            InstalledRoles = capabilities.InstalledRoles
        }, SerializerOptions);

        var now = DateTimeOffset.UtcNow;

        await _repository.ExecuteUpdateAsync<Machine>(
            m => m.Id == machineId,
            s => s.SetProperty(m => m.RuntimeCapabilitiesJson, json)
                  .SetProperty(m => m.RuntimeCapabilitiesUpdatedAt, (DateTimeOffset?)now),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(int MachineId, MachineRuntimeCapabilities Capabilities)>> LoadAllAsync(CancellationToken ct)
    {
        // Project to (id, json) tuple — avoid full entity hydration just for
        // capability data on startup. Skip rows with NULL json (the migration
        // backward-compat case + machines that never health-checked).
        var rows = await _repository.QueryNoTracking<Machine>(m => m.RuntimeCapabilitiesJson != null)
            .Select(m => new { m.Id, m.RuntimeCapabilitiesJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var results = new List<(int, MachineRuntimeCapabilities)>(rows.Count);

        foreach (var row in rows)
        {
            try
            {
                var persisted = JsonSerializer.Deserialize<PersistedCapabilities>(row.RuntimeCapabilitiesJson, SerializerOptions);
                if (persisted == null) continue;

                results.Add((row.Id, new MachineRuntimeCapabilities
                {
                    Os = persisted.Os ?? string.Empty,
                    OsVersion = persisted.OsVersion ?? string.Empty,
                    DefaultShell = persisted.DefaultShell ?? string.Empty,
                    InstalledShells = persisted.InstalledShells ?? string.Empty,
                    Architecture = persisted.Architecture ?? string.Empty,
                    AgentVersion = persisted.AgentVersion ?? string.Empty,
                    SupportedServices = persisted.SupportedServices ?? Array.Empty<string>(),
                    InstalledRoles = persisted.InstalledRoles ?? string.Empty
                }));
            }
            catch (JsonException ex)
            {
                // One corrupted row can't block startup. Log + skip so the
                // operator can drop the bad blob (or wait for next health
                // check to overwrite it) without rebuilding the server image.
                Log.Warning(ex,
                    "Failed to deserialise persisted runtime capabilities for machine {MachineId} — skipping. " +
                    "The cache will repopulate from the next successful health-check probe.",
                    row.Id);
            }
        }

        return results;
    }

    public Task InvalidateAsync(int machineId, CancellationToken ct)
    {
        // NULL both columns atomically. Caller (MachineUpgradeService after
        // successful upgrade) wants the NEXT health check to be the source
        // of truth, not the pre-upgrade snapshot.
        return _repository.ExecuteUpdateAsync<Machine>(
            m => m.Id == machineId,
            s => s.SetProperty(m => m.RuntimeCapabilitiesJson, (string)null)
                  .SetProperty(m => m.RuntimeCapabilitiesUpdatedAt, (DateTimeOffset?)null),
            ct);
    }

    /// <summary>
    /// Internal DTO that pins the on-disk JSON shape so future refactors of
    /// <see cref="MachineRuntimeCapabilities"/> (adding a property, renaming)
    /// don't silently break round-trip. New fields here MUST stay nullable for
    /// backward compat with pre-existing JSON blobs that don't have them.
    /// </summary>
    internal sealed class PersistedCapabilities
    {
        public string Os { get; set; }
        public string OsVersion { get; set; }
        public string DefaultShell { get; set; }
        public string InstalledShells { get; set; }
        public string Architecture { get; set; }
        public string AgentVersion { get; set; }
        public string[] SupportedServices { get; set; }

        /// <summary>
        /// H7 — comma-separated list of detected system roles
        /// (e.g. <c>"iis,docker"</c>). Nullable for backward-compat: blobs
        /// written by pre-H7 servers don't have this field; the LoadAllAsync
        /// projection coalesces null to empty string.
        /// </summary>
        public string InstalledRoles { get; set; }
    }
}
