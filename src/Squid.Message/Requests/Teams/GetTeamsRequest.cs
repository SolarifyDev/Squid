using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Requests.Teams;

public class GetTeamsRequest : IRequest
{
    public int SpaceId { get; set; }
}

public class GetTeamsResponse : SquidResponse<GetTeamsResponseData>
{
}

public class GetTeamsResponseData
{
    public List<TeamDto> Teams { get; set; }
}
