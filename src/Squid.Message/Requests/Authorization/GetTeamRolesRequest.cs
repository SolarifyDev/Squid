using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Requests.Authorization;

[RequiresPermission(Permission.TeamView)]
public class GetTeamRolesRequest : IRequest
{
    public int TeamId { get; set; }
}

public class GetTeamRolesResponse : SquidResponse<List<ScopedUserRoleDto>>
{
}
