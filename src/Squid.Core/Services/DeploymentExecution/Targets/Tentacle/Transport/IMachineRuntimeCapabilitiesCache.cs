using System.Collections.Concurrent;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Process-local cache of per-machine runtime capability metadata (OS, shells,
/// agent version). Populated by <see cref="TentacleHealthCheckStrategy"/> on
/// every health check; read by <see cref="TentacleEndpointVariableContributor"/>
/// to enrich the deployment variable set so intent renderers can pick the right
/// script syntax for Linux vs Windows targets without an extra round-trip per
/// deployment. Cold cache → empty metadata (contributor falls back to defaults).
/// A later iteration can persist this alongside the Machine entity to survive
/// server restarts; for now an in-memory cache is sufficient since the health
/// check runs on schedule and on-demand.
/// </summary>
public interface IMachineRuntimeCapabilitiesCache
{
    void Store(int machineId, IReadOnlyDictionary<string, string> metadata, string agentVersion);

    MachineRuntimeCapabilities TryGet(int machineId);

    /// <summary>
    /// Drop the cached entry for a machine so the next health check re-reads
    /// it from the agent's Capabilities probe. Called after a successful
    /// upgrade so the server doesn't keep reporting the agent's old version
    /// for up to a full health-check interval — without this, the upgrade
    /// would appear to "fail to take" in the UI even though the agent is
    /// happily running the new binary.
    /// </summary>
    void Invalidate(int machineId);
}

public sealed class MachineRuntimeCapabilities
{
    public string Os { get; init; } = string.Empty;
    public string OsVersion { get; init; } = string.Empty;
    public string DefaultShell { get; init; } = string.Empty;
    public string InstalledShells { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = string.Empty;

    public static readonly MachineRuntimeCapabilities Empty = new();
}

public sealed class InMemoryMachineRuntimeCapabilitiesCache : IMachineRuntimeCapabilitiesCache, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, MachineRuntimeCapabilities> _cache = new();

    public void Store(int machineId, IReadOnlyDictionary<string, string> metadata, string agentVersion)
    {
        if (metadata == null) return;

        var caps = new MachineRuntimeCapabilities
        {
            Os = Read(metadata, "os"),
            OsVersion = Read(metadata, "osVersion"),
            DefaultShell = Read(metadata, "defaultShell"),
            InstalledShells = Read(metadata, "installedShells"),
            Architecture = Read(metadata, "architecture"),
            AgentVersion = agentVersion ?? string.Empty
        };
        _cache[machineId] = caps;
    }

    public MachineRuntimeCapabilities TryGet(int machineId)
        => _cache.TryGetValue(machineId, out var caps) ? caps : MachineRuntimeCapabilities.Empty;

    public void Invalidate(int machineId) => _cache.TryRemove(machineId, out _);

    private static string Read(IReadOnlyDictionary<string, string> meta, string key)
        => meta.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
}
