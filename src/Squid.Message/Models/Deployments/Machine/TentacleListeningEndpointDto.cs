namespace Squid.Message.Models.Deployments.Machine;

public class TentacleListeningEndpointDto
{
    public string CommunicationStyle { get; set; } = "TentacleListening";
    public string Uri { get; set; }
    public string Thumbprint { get; set; }
    public string AgentVersion { get; set; }
    public int? ProxyId { get; set; }
}
