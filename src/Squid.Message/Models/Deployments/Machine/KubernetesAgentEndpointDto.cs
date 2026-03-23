namespace Squid.Message.Models.Deployments.Machine;

public class KubernetesAgentEndpointDto
{
    public string CommunicationStyle { get; set; } = "KubernetesAgent";
    public string SubscriptionId { get; set; }
    public string Thumbprint { get; set; }
    public string Namespace { get; set; }
    public string ReleaseName { get; set; }
    public string HelmNamespace { get; set; }
    public string ChartRef { get; set; }
    public string AgentVersion { get; set; }
}
