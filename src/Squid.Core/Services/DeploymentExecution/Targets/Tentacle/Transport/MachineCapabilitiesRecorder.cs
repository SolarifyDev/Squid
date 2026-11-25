using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Writes one agent <see cref="CapabilitiesResponse"/> to every place a machine's capabilities live: the
/// process-local cache this pod dispatches from and the persisted copy other pods (and the next restart)
/// read. Every on-demand refresh and the Kubernetes agent health check go through here, so a transport can
/// never advertise protocol versions in one place and not the other.
/// </summary>
public interface IMachineCapabilitiesRecorder : IScopedDependency
{
    /// <summary>Stores the response in the cache and persists it. A failed persist is logged and swallowed: this pod's cache is already current and the next probe retries the write.</summary>
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
        var persistedAt = await PersistAsync(machineId, response, ct).ConfigureAwait(false);

        _cache.Store(machineId, response.Metadata ?? new Dictionary<string, string>(), response.AgentVersion, response.SupportedServices, persistedAt);

        return _cache.TryGet(machineId);
    }

    /// <summary>The timestamp the row was stamped with (what <c>LoadIfNewerAsync</c> compares against), or null when the write failed and the cache falls back to the local clock.</summary>
    private async Task<DateTimeOffset?> PersistAsync(int machineId, CapabilitiesResponse response, CancellationToken ct)
    {
        try
        {
            return await _store.RunAsync(persistence => persistence.SaveAsync(machineId, Map(response), ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to persist runtime capabilities for machine {MachineId}; this pod's cache is current and the next probe retries the write.", machineId);

            return null;
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
