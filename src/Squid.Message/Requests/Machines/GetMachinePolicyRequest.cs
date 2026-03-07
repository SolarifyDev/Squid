using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

public class GetMachinePolicyRequest : IRequest
{
    public int Id { get; set; }
}

public class GetMachinePolicyResponse : SquidResponse<MachinePolicyDto>
{
}
