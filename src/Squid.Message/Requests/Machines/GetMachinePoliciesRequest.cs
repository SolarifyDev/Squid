using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

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
