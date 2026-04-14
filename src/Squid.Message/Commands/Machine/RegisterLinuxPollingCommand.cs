using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class RegisterLinuxPollingCommand : ICommand, ISpaceScoped, IMachinePolicyScoped
{
    public string MachineName { get; set; }
    public string Thumbprint { get; set; }
    public string SubscriptionId { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public string Roles { get; set; }
    public string Environments { get; set; }
    public string AgentVersion { get; set; }
    public int? MachinePolicyId { get; set; }
}
