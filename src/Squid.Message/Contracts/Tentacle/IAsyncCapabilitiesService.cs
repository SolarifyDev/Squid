namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// Async proxy interface for the client side (HalibutRuntime.CreateAsyncClient).
/// Methods must NOT include CancellationToken — Halibut routes calls by matching async methods
/// to their sync counterparts in ICapabilitiesService, and does not strip CancellationToken.
/// </summary>
public interface IAsyncCapabilitiesService
{
    Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request);
}
