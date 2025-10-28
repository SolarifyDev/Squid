using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

public class GetReleasesRequest : IPaginatedRequest
{
    public int PageIndex { get; set; }
    
    public int PageSize { get; set; }
    
    public int ChannelId { get; set; }
}

public class GetReleasesResponse : SquidResponse<GetReleasesResponseData>
{
}

public class GetReleasesResponseData
{
    public int Count { get; set; }
    
    public List<ReleaseDto> Releases { get; set; }
    
    public List<ReleaseDto> CurrentDeployedReleases { get; set; }
}
