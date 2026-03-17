using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

[RequiresPermission(Permission.TeamEdit)]
public class AddTeamMemberCommand : ICommand
{
    public int TeamId { get; set; }
    public int UserId { get; set; }
}

public class AddTeamMemberResponse : SquidResponse
{
}
