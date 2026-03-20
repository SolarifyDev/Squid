using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.TeamEdit)]
public class AssignRoleToTeamCommand : ICommand
{
    public int TeamId { get; set; }
    public int UserRoleId { get; set; }
    public int? SpaceId { get; set; }
}

public class AssignRoleToTeamResponse : SquidResponse<ScopedUserRoleDto>
{
}
