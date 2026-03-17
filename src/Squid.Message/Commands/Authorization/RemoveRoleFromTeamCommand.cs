using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.TeamEdit)]
public class RemoveRoleFromTeamCommand : ICommand
{
    public int TeamId { get; set; }
    public int ScopedUserRoleId { get; set; }
}

public class RemoveRoleFromTeamResponse : SquidResponse
{
}
