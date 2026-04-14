namespace Squid.Message.Models.Deployments.Machine;

public class TentaclePollingEndpointDto
{
    public string CommunicationStyle { get; set; } = "TentaclePolling";
    public string SubscriptionId { get; set; }
    public string Thumbprint { get; set; }
    public string AgentVersion { get; set; }
}
