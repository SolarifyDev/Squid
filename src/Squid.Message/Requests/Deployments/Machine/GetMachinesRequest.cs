using Squid.Message.Response;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Message.Requests.Deployments.Machine;

public class GetMachinesRequest : IPaginatedRequest
{
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}

public class GetMachinesResponse : SquidResponse<GetMachinesResponseData>
{
}

public class GetMachinesResponseData
{
    public int Count { get; set; }

    public List<MachineDto> Machines { get; set; }
} 