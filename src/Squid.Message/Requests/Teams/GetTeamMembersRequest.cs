using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Requests.Teams;

public class GetTeamMembersRequest : IRequest
{
    public int TeamId { get; set; }
}

public class GetTeamMembersResponse : SquidResponse<GetTeamMembersResponseData>
{
}

public class GetTeamMembersResponseData
{
    public List<TeamMemberDto> Members { get; set; }
}
