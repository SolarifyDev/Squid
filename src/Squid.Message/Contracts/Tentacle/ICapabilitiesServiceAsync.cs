using Halibut.Transport.Caching;

namespace Squid.Message.Contracts.Tentacle;

public interface ICapabilitiesServiceAsync
{
    /// <summary>
    /// P1-Phase9.7: see <see cref="ICapabilitiesService.GetCapabilities"/> for
    /// rationale. Mirror cache TTL kept in sync via Rule-8 pin test.
    /// </summary>
    [CacheResponse(60)]
    Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct);
}
