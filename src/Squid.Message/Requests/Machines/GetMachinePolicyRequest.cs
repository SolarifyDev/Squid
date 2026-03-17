using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

[RequiresPermission(Permission.MachineView)]
public class GetMachinePolicyRequest : IRequest
{
    public int Id { get; set; }
}

public class GetMachinePolicyResponse : SquidResponse<MachinePolicyDto>
{
}
