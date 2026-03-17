using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

public class CreateTeamCommand : ICommand
{
    public string Name { get; set; }
    public string Description { get; set; }
    public int SpaceId { get; set; }
}

public class CreateTeamResponse : SquidResponse<CreateTeamResponseData>
{
}

public class CreateTeamResponseData
{
    public TeamDto Team { get; set; }
}
