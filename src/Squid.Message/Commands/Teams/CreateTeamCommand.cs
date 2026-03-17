using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

[RequiresPermission(Permission.TeamCreate)]
public class CreateTeamCommand : ICommand
{
    public string Name { get; set; }
    public string Description { get; set; }
    public int SpaceId { get; set; }
}

public class CreateTeamResponse : SquidResponse<TeamDto>
{
}
