using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

[RequiresPermission(Permission.TeamDelete)]
public class DeleteTeamCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteTeamResponse : SquidResponse
{
}
