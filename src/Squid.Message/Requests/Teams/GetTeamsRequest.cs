using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Requests.Teams;

[RequiresPermission(Permission.TeamView)]
public class GetTeamsRequest : IRequest
{
    public int SpaceId { get; set; }
}

public class GetTeamsResponse : SquidResponse<List<TeamDto>>
{
}
