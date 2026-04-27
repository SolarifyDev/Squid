using Halibut.Transport.Caching;

namespace Squid.Message.Contracts.Tentacle;

public interface ICapabilitiesService
{
    /// <summary>
    /// P1-Phase9.7: Halibut response cache (60s TTL).
    ///
    /// <para><b>Why cache</b>: <see cref="CapabilitiesResponse"/> is built by
    /// the agent's <c>RuntimeCapabilitiesInspector</c> which reads three on-disk
    /// files per call (<c>last-upgrade.json</c>, etc.) plus shells out to
    /// detect installed scripting runtimes. The server polls this every health
    /// check (typically 30-60s per agent across N agents) — without cache, that's
    /// N × per-second disk IO on every agent.</para>
    ///
    /// <para><b>60s TTL chosen</b>: agent capabilities change rarely (only on
    /// upgrade or shell-runtime install). 60s gives operators a meaningful
    /// freshness signal post-upgrade without making routine health checks
    /// pummel agent disks. OctopusTentacle's equivalent uses 600s (10 min);
    /// Squid uses 60s because we ALSO ship richer Metadata in the response
    /// (upgrade-status from disk file) that operators want to see freshly.</para>
    /// </summary>
    [CacheResponse(60)]
    CapabilitiesResponse GetCapabilities(CapabilitiesRequest request);
}
