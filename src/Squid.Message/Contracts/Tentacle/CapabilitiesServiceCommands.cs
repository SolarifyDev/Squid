namespace Squid.Message.Contracts.Tentacle;

public class CapabilitiesRequest
{
}

public class CapabilitiesResponse
{
    public List<string> SupportedServices { get; set; } = new();

    public string AgentVersion { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = new();
}
