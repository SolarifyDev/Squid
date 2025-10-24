using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

public class GetReleasesRequest : IRequest
{
    
}

public class GetReleasesResponse : SquidResponse<GetReleasesResponseData>
{
}

public class GetReleasesResponseData
{
    public int Count { get; set; }
    
    public List<ReleaseDto> Releases { get; set; }
}
