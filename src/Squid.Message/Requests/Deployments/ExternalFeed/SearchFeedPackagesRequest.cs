using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedView)]
public class SearchFeedPackagesRequest : IRequest
{
    public int FeedId { get; set; }
    public string Query { get; set; }
    public int Take { get; set; } = 20;
}

public class SearchFeedPackagesResponse : SquidResponse<SearchFeedPackagesResponseData>
{
}

public class SearchFeedPackagesResponseData
{
    public List<string> Packages { get; set; }
}
