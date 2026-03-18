using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class SaveMachinePolicyCommand : ICommand, ISpaceScoped
{
    public MachinePolicyDto MachinePolicy { get; set; }
    int? ISpaceScoped.SpaceId => MachinePolicy?.SpaceId;
}

public class SaveMachinePolicyResponse : SquidResponse<MachinePolicyDto>
{
}
