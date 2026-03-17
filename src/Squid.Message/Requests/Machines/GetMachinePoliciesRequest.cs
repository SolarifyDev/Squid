using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

[RequiresPermission(Permission.MachineView)]
public class GetMachinePoliciesRequest : IRequest
{
}

public class GetMachinePoliciesResponse : SquidResponse<GetMachinePoliciesResponseData>
{
}

public class GetMachinePoliciesResponseData
{
    public List<MachinePolicyDto> MachinePolicies { get; set; }
}
