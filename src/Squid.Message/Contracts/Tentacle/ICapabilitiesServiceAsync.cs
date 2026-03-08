namespace Squid.Message.Contracts.Tentacle;

public interface ICapabilitiesServiceAsync
{
    Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct);
}
