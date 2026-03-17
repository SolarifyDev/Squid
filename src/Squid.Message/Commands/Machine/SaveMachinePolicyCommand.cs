using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class SaveMachinePolicyCommand : ICommand
{
    public MachinePolicyDto MachinePolicy { get; set; }
}

public class SaveMachinePolicyResponse : SquidResponse<MachinePolicyDto>
{
}
