using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Requests.Teams;

[RequiresPermission(Permission.TeamView)]
public class GetTeamRequest : IRequest
{
    public int Id { get; set; }
}

public class GetTeamResponse : SquidResponse<TeamDto>
{
}
