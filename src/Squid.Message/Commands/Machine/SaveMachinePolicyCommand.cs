using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class SaveMachinePolicyCommand : ICommand
{
    public MachinePolicyDto MachinePolicy { get; set; }
}

public class SaveMachinePolicyResponse : SquidResponse<MachinePolicyDto>
{
}
