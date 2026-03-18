using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

[RequiresPermission(Permission.MachineView)]
public class GetMachinePolicyRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class GetMachinePolicyResponse : SquidResponse<MachinePolicyDto>
{
}
