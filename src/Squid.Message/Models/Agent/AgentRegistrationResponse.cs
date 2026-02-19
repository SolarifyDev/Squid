namespace Squid.Message.Models.Agent;

public class AgentRegistrationResponse
{
    public int MachineId { get; set; }
    public string ServerThumbprint { get; set; }
    public string SubscriptionUri { get; set; }
}
