using System.Collections.Concurrent;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;

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
    /// <summary>
    /// P0-E.3 (2026-04-24 audit): signature extended to accept
    /// <paramref name="supportedServices"/> — the list of service interfaces the
    /// agent implements (e.g. <c>"IScriptService/v1"</c>, eventually
    /// <c>"IScriptService/v2"</c>). Used by the execution strategy to pick the
    /// highest common protocol version without an extra capabilities RPC at
    /// dispatch time. Prerequisite for the future E.2 V2 rollout.
    /// </summary>
    void Store(int machineId, IReadOnlyDictionary<string, string> metadata, string agentVersion, IReadOnlyList<string> supportedServices = null);

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

    /// <summary>
    /// H7 — comma-separated list of installed system roles detected on the
    /// target (e.g. <c>"iis,docker"</c>). Projected by
    /// <see cref="Validation.MachineCapabilitySet"/> into per-role
    /// <c>role:{name}</c> slots so handlers can AND-require multiple roles.
    /// Empty when the agent doesn't yet report roles (older agent / H7 not
    /// installed) — the validator treats absent slots as optimistic-allow.
    /// </summary>
    public string InstalledRoles { get; init; } = string.Empty;

    /// <summary>
    /// P0-E.3: service-interface strings reported by the agent in
    /// <c>CapabilitiesResponse.SupportedServices</c> (e.g. <c>"IScriptService/v1"</c>).
    /// Populated on every health check. Used by <see cref="SupportsScriptServiceV2"/>
    /// to drive the V1/V2 dispatch decision without a second capabilities round-trip.
    /// </summary>
    public IReadOnlyList<string> SupportedServices { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True if the agent has announced <c>IScriptService/v2</c> in its capabilities
    /// list. Prep work for E.2 — the V2 server-side dispatch is not yet implemented,
    /// but once it is this read site is already wired. Cold cache → returns
    /// <c>false</c>, so dispatch safely falls back to V1.
    /// </summary>
    public bool SupportsScriptServiceV2 =>
        SupportedServices.Any(s => string.Equals(s, "IScriptService/v2", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// explicit-claim OS predicates the upgrade-strategy
    /// resolvers + version registry use INSTEAD of inline string comparisons.
    /// Each consumer reads <c>capabilities.IsWindows</c> / <c>IsLinux</c> /
    /// <c>IsMacOS</c> / <c>IsUnknown</c>; the literal strings live ONCE in
    /// <see cref="AgentOperatingSystems"/>. Without these, the agent-side
    /// <c>"Windows"</c> token was duplicated across 4+ consumer sites and
    /// any rename silently broke OS routing.
    ///
    /// <para><b>Long-form Windows tolerance</b>: delegates to
    /// <see cref="WindowsOsStringHelper.IsWindows"/> so that legacy agent
    /// metadata carrying <c>"Microsoft Windows NT 10.0.19045.0"</c> (the raw
    /// <c>Environment.OSVersion.VersionString</c> form, written by older
    /// Tentacle binaries / out-of-band callers) still routes correctly.
    /// Before this delegation, strict equality with the canonical short form
    /// <c>"Windows"</c> caused <see cref="WindowsTentacleUpgradeStrategy"/>'s
    /// <c>CanHandle</c> to reject ALL long-form Windows machines, while the
    /// Linux strategy's <c>IsLinux || IsUnknown</c> ALSO rejected them
    /// (because long form is neither empty nor exactly <c>"Unknown"</c>) —
    /// the operator-visible symptom was <c>"CommunicationStyle
    /// 'TentaclePolling' is not supported for in-UI upgrades"</c> on a
    /// perfectly healthy Windows agent. Single source of truth for "is this
    /// a Windows host?" now lives in <see cref="WindowsOsStringHelper"/>,
    /// shared with the IIS dispatch guard + <c>MachineCapabilitySet</c>
    /// projection (PR #348).</para>
    /// </summary>
    public bool IsWindows => WindowsOsStringHelper.IsWindows(Os);

    /// <summary>True when agent reported Linux OS (any distro) via Capabilities probe.</summary>
    public bool IsLinux => string.Equals(Os, AgentOperatingSystems.Linux, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when agent reported macOS via Capabilities probe. No upgrade strategy claims this today; a future <c>MacOSTentacleUpgradeStrategy</c> would plug in without modifying the Linux strategy.</summary>
    public bool IsMacOS => string.Equals(Os, AgentOperatingSystems.MacOS, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when capabilities haven't been populated yet (cold cache, agent
    /// hasn't reported via Capabilities probe, or agent reported the
    /// fallback <see cref="AgentOperatingSystems.Unknown"/> sentinel because
    /// neither <c>OperatingSystem.IsWindows()</c>, <c>IsLinux()</c>, nor
    /// <c>IsMacOS()</c> matched). The Linux upgrade strategy claims this
    /// case (<c>IsLinux || IsUnknown</c>) as the historical default to
    /// preserve behaviour for the existing operator base —
    ///  there was no OS axis at all and Linux was the only
    /// strategy. <see cref="string.IsNullOrWhiteSpace"/> covers both the
    /// empty cold-cache path and the explicit "Unknown" fallback path.
    /// </summary>
    public bool IsUnknown => string.IsNullOrWhiteSpace(Os)
        || string.Equals(Os, AgentOperatingSystems.Unknown, StringComparison.OrdinalIgnoreCase);

    public static readonly MachineRuntimeCapabilities Empty = new();
}

public sealed class InMemoryMachineRuntimeCapabilitiesCache : IMachineRuntimeCapabilitiesCache, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, MachineRuntimeCapabilities> _cache = new();

    public void Store(int machineId, IReadOnlyDictionary<string, string> metadata, string agentVersion, IReadOnlyList<string> supportedServices = null)
    {
        if (metadata == null) return;

        var caps = new MachineRuntimeCapabilities
        {
            Os = Read(metadata, "os"),
            OsVersion = Read(metadata, "osVersion"),
            DefaultShell = Read(metadata, "defaultShell"),
            InstalledShells = Read(metadata, "installedShells"),
            Architecture = Read(metadata, "architecture"),
            AgentVersion = agentVersion ?? string.Empty,
            SupportedServices = supportedServices ?? Array.Empty<string>(),
            InstalledRoles = Read(metadata, "installedRoles")
        };
        _cache[machineId] = caps;
    }

    public MachineRuntimeCapabilities TryGet(int machineId)
        => _cache.TryGetValue(machineId, out var caps) ? caps : MachineRuntimeCapabilities.Empty;

    public void Invalidate(int machineId) => _cache.TryRemove(machineId, out _);

    private static string Read(IReadOnlyDictionary<string, string> meta, string key)
        => meta.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
}
