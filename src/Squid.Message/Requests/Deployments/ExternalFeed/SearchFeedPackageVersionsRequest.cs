using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedView)]
public class SearchFeedPackageVersionsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int FeedId { get; set; }
    public string PackageId { get; set; }
    public int Take { get; set; } = 30;
    public bool IncludePreRelease { get; set; }
    public string Filter { get; set; }
}

public class SearchFeedPackageVersionsResponse : SquidResponse<SearchFeedPackageVersionsResponseData>
{
}

public class SearchFeedPackageVersionsResponseData
{
    public List<string> Versions { get; set; }
}
