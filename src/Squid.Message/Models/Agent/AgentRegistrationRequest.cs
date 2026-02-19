namespace Squid.Message.Models.Agent;

public class AgentRegistrationRequest
{
    public string MachineName { get; set; }
    public string Thumbprint { get; set; }
    public string SubscriptionId { get; set; }
    public int SpaceId { get; set; } = 1;
    public string Roles { get; set; }
    public string EnvironmentIds { get; set; }
    public string Namespace { get; set; } = "default";
}
