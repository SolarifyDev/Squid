using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;
using Squid.Message.Models.Deployments.ExternalFeed;

namespace Squid.Message.Requests.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedView)]
public class GetExternalFeedsRequest : IPaginatedRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}

public class GetExternalFeedsResponse : SquidResponse<GetExternalFeedsResponseData>
{
}

public class GetExternalFeedsResponseData
{
    public int Count { get; set; }

    public List<ExternalFeedDto> ExternalFeeds { get; set; }
} 