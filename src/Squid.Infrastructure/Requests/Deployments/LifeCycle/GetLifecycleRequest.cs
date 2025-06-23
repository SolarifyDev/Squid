using Squid.Core.Models.Deployments.LifeCycle;
using Squid.Core.Response;

namespace Squid.Core.Requests.Deployments.LifeCycle;

public class GetLifecycleRequest : IPaginatedRequest
{
    public int PageIndex { get; set; }
    
    public int PageSize { get; set; }
    
    public string Keyword { get; set; }
}

public class GetLifeCycleResponse : SquidResponse<GetLifeCycleResponseData>
{
}

public class GetLifeCycleResponseData
{
    public int Count { get; set; }
    
    public List<LifecyclePhaseDto> LifeCycles { get; set; }
}