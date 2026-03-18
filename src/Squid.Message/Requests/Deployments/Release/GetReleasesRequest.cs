using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

[RequiresPermission(Permission.ReleaseView)]
public class GetReleasesRequest : IPaginatedRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;
    
    public int ChannelId { get; set; }
    
    public int ProjectId { get; set; }
}

public class GetReleasesResponse : SquidResponse<GetReleasesResponseData>
{
}

public class GetReleasesResponseData
{
    public int Count { get; set; }
    
    public List<ReleaseDto> Releases { get; set; }
    
    public List<int> CurrentDeployedReleaseIds { get; set; }
}
