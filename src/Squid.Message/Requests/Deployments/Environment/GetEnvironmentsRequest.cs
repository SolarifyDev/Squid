using Squid.Message.Response;
using Squid.Message.Models.Deployments.Environment;

namespace Squid.Message.Requests.Deployments.Environment;

public class GetEnvironmentsRequest : IPaginatedRequest
{
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}

public class GetEnvironmentsResponse : SquidResponse<GetEnvironmentsResponseData>
{
}

public class GetEnvironmentsResponseData
{
    public int Count { get; set; }

    public List<EnvironmentDto> Environments { get; set; }
}
