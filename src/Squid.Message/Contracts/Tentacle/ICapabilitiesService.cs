namespace Squid.Message.Contracts.Tentacle;

public interface ICapabilitiesService
{
    CapabilitiesResponse GetCapabilities(CapabilitiesRequest request);
}
