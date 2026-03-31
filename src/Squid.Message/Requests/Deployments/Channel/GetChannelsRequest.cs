using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Channel;

[RequiresPermission(Permission.ChannelView)]
public class GetChannelsRequest : IPaginatedRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int? ProjectId { get; set; }
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    public string Keyword { get; set; }
}

public class GetChannelsResponse : SquidResponse<GetChannelsResponseData>
{
}

public class GetChannelsResponseData
{
    public int Count { get; set; }
    
    public List<ChannelDto> Channels { get; set; }
}