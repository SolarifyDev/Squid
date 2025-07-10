using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Channel;

public class GetChannelsRequest : IPaginatedRequest
{
    public int PageIndex { get; set; }
    
    public int PageSize { get; set; }
    
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