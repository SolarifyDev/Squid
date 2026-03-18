using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class RegisterKubernetesAgentCommand : ICommand, ISpaceScoped
{
    public string MachineName { get; set; }
    public string Thumbprint { get; set; }
    public string SubscriptionId { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public string Roles { get; set; }
    public string Environments { get; set; }
    public string Namespace { get; set; } = "default";
    public string AgentVersion { get; set; }
    public string ReleaseName { get; set; }
    public string HelmNamespace { get; set; }
    public string ChartRef { get; set; }
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
