using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Writes one agent <see cref="CapabilitiesResponse"/> to every place a machine's capabilities live: the
/// process-local cache this pod dispatches from and the persisted copy other pods (and the next restart)
/// read. Every on-demand refresh and every agent health check (Tentacle and Kubernetes) go through here, so a transport can
/// never advertise protocol versions in one place and not the other.
/// </summary>
public interface IMachineCapabilitiesRecorder : IScopedDependency
{
    /// <summary>
    /// Persists the response and stores it in the cache, stamped with the persisted row's timestamp. A failed persist is logged and swallowed, and a cancelled
    /// one is rethrown, but either way this pod still caches the agent's answer, stamped with <see cref="MachineRuntimeCapabilitiesPersistence.NotStored"/> so
    /// any row another pod stores (an invalidation after an upgrade or downgrade) outranks it on the next sync. The next probe retries the write.
    /// </summary>
    Task<MachineRuntimeCapabilities> RecordAsync(int machineId, CapabilitiesResponse response, CancellationToken ct);
}

public sealed class MachineCapabilitiesRecorder : IMachineCapabilitiesRecorder
{
    private readonly IMachineRuntimeCapabilitiesCache _cache;
    private readonly IMachineCapabilitiesStoreAccess _store;

    public MachineCapabilitiesRecorder(IMachineRuntimeCapabilitiesCache cache, IMachineCapabilitiesStoreAccess store)
    {
        _cache = cache;
        _store = store;
    }

    public async Task<MachineRuntimeCapabilities> RecordAsync(int machineId, CapabilitiesResponse response, CancellationToken ct)
    {
        var persistedAt = MachineRuntimeCapabilitiesPersistence.NotStored;

        try
        {
            persistedAt = await PersistAsync(machineId, response, ct).ConfigureAwait(false);
        }
        finally
        {
            _cache.Store(machineId, response.Metadata ?? new Dictionary<string, string>(), response.AgentVersion, response.SupportedServices, persistedAt);
        }

        return _cache.TryGet(machineId);
    }

    /// <summary>The timestamp the row was stamped with (what <c>LoadIfNewerAsync</c> compares against), or <see cref="MachineRuntimeCapabilitiesPersistence.NotStored"/> when the write failed.</summary>
    private async Task<DateTimeOffset> PersistAsync(int machineId, CapabilitiesResponse response, CancellationToken ct)
    {
        try
        {
            return await _store.RunAsync(persistence => persistence.SaveAsync(machineId, Map(response), ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warning(ex, "Failed to persist runtime capabilities for machine {MachineId}; this pod caches the answer below any stored row and the next probe retries the write.", machineId);

            return MachineRuntimeCapabilitiesPersistence.NotStored;
        }
    }

    /// <summary>The canonical <see cref="MachineRuntimeCapabilities"/> the persistence layer serialises, built from the agent's own metadata keys (the same ones the cache reads).</summary>
    internal static MachineRuntimeCapabilities Map(CapabilitiesResponse response) => new()
    {
        Os = ReadMetadata(response, "os"),
        OsVersion = ReadMetadata(response, "osVersion"),
        DefaultShell = ReadMetadata(response, "defaultShell"),
        InstalledShells = ReadMetadata(response, "installedShells"),
        Architecture = ReadMetadata(response, "architecture"),
        AgentVersion = response.AgentVersion ?? string.Empty,
        SupportedServices = (IReadOnlyList<string>)response.SupportedServices ?? Array.Empty<string>(),
        InstalledRoles = ReadMetadata(response, "installedRoles")
    };

    private static string ReadMetadata(CapabilitiesResponse response, string key)
        => response.Metadata != null && response.Metadata.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
}
