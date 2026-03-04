using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class RegisterKubernetesAgentCommand : ICommand
{
    public string MachineName { get; set; }
    public string Thumbprint { get; set; }
    public string SubscriptionId { get; set; }
    public int SpaceId { get; set; }
    public string Roles { get; set; }
    public string Environments { get; set; }
    public string Namespace { get; set; } = "default";
}

public class RegisterMachineResponse : SquidResponse<RegisterMachineResponseData>
{
}

public class RegisterMachineResponseData
{
    public int MachineId { get; set; }
    public string ServerThumbprint { get; set; }
    public string SubscriptionUri { get; set; }
}
