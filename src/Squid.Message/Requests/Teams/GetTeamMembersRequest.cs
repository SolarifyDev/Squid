using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Requests.Teams;

[RequiresPermission(Permission.TeamView)]
public class GetTeamMembersRequest : IRequest
{
    public int TeamId { get; set; }
}

public class GetTeamMembersResponse : SquidResponse<List<TeamMemberDto>>
{
}
