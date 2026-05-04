using Squid.Core.Services.DeploymentExecution.Tentacle;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Single-responsibility lookup: "what is the latest published Tentacle
/// version for a given communication style + agent capabilities".
///
/// <para>
/// Per-platform delivery concerns (download URL pattern, archive format,
/// signing, mirror configuration) belong to each <see cref="IMachineUpgradeStrategy"/>
/// — putting them here would couple the registry to every transport
/// flavour and violate ISP. Keeping this interface narrow lets new
/// transports (Windows zip, Helm OCI, custom mirror) plug in without
/// touching the registry.
/// </para>
///
/// <para><b>P1-Phase12.E.4 widening:</b> <see cref="GetLatestVersionAsync"/>
/// takes <see cref="MachineRuntimeCapabilities"/> in addition to the
/// communication-style string. Linux + Windows tentacles share the SAME
/// wire-protocol communication styles (<c>TentaclePolling</c> /
/// <c>TentacleListening</c>) — Halibut doesn't distinguish them at the
/// protocol layer — so style alone can't pick between Linux Docker Hub
/// (the Linux binary version source) and GitHub Releases (the Windows zip
/// version source). The agent OS, reported via the health-check capabilities
/// probe and cached in <see cref="IMachineRuntimeCapabilitiesCache"/>, is
/// what differentiates them. Cold cache → empty
/// <see cref="MachineRuntimeCapabilities.Os"/>; the registry routes that
/// case to the Linux Docker Hub source as the historical default
/// (mirroring the OS-aware strategy resolver's same default in
/// <c>LinuxTentacleUpgradeStrategy.CanHandle</c>).</para>
///
/// <para><b>Resolution chain</b> (first non-empty wins):</para>
/// <list type="number">
///   <item>Per-style env var override — operator escape hatch.</item>
///   <item>Fresh in-process TTL cache.</item>
///   <item>Live source query (Docker Hub for Linux/K8s tentacles, GitHub
///         Releases for Windows tentacles).</item>
///   <item>Stale cache fallback when the live source is unreachable —
///         better-than-refusal degraded mode.</item>
///   <item>Empty + warning. Caller must surface the gap to the operator.</item>
/// </list>
/// </summary>
public interface ITentacleVersionRegistry : IScopedDependency
{
    Task<string> GetLatestVersionAsync(string communicationStyle, MachineRuntimeCapabilities capabilities, CancellationToken ct);
}

// Lifetime: SCOPED, not singleton. The implementation injects
// ISquidHttpClientFactory which is itself IScopedDependency (it captures
// the request's ILifetimeScope to resolve IHttpClientFactory). Holding a
// captured scoped service inside a singleton would keep the first
// request's scope alive forever and silently fall back to `new HttpClient()`
// once that scope is disposed (the classic socket-exhaustion bug).
//
// The Docker-Hub TTL cache stays process-wide via a `static` field on
// the implementation, so we keep the perf benefit (one query per 10 min,
// not one per request) without paying the captive-dependency cost.
