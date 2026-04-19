namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Single-responsibility lookup: "what is the latest published Tentacle
/// version for a given communication style".
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
/// <para><b>Resolution chain</b> (first non-empty wins):</para>
/// <list type="number">
///   <item>Per-style env var override — operator escape hatch.</item>
///   <item>Fresh in-process TTL cache.</item>
///   <item>Live Docker Hub query of the per-style image repository.</item>
///   <item>Stale cache fallback when Docker Hub is unreachable —
///         better-than-refusal degraded mode.</item>
///   <item>Empty + warning. Caller must surface the gap to the operator.</item>
/// </list>
/// </summary>
public interface ITentacleVersionRegistry : IScopedDependency
{
    Task<string> GetLatestVersionAsync(string communicationStyle, CancellationToken ct);
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
