using Squid.Message.Response;

namespace Squid.Message.Commands.Agent;

public class RegisterAgentCommand : ICommand
{
    public string MachineName { get; set; }
    public string Thumbprint { get; set; }
    public string SubscriptionId { get; set; }
    public int SpaceId { get; set; } = 1;
    public string Roles { get; set; }
    public string EnvironmentIds { get; set; }
    public string Namespace { get; set; } = "default";
}

public class RegisterAgentResponse : SquidResponse<RegisterAgentResponseData>
{
}

public class RegisterAgentResponseData
{
    public int MachineId { get; set; }
    public string ServerThumbprint { get; set; }
    public string SubscriptionUri { get; set; }
}
