using Squid.Message.Models.Deployments.TargetTag;
using Squid.Message.Response;

namespace Squid.Message.Requests.TargetTag;

public class GetTargetTagsRequest : IRequest
{
}

public class GetTargetTagsResponse : SquidResponse<GetTargetTagsResponseData>
{
}

public class GetTargetTagsResponseData
{
    public List<TargetTagDto> TargetTags { get; set; }
}
