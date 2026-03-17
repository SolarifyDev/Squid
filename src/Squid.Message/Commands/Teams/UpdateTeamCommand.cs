using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

public class UpdateTeamCommand : ICommand
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public int SpaceId { get; set; }
}

public class UpdateTeamResponse : SquidResponse<UpdateTeamResponseData>
{
}

public class UpdateTeamResponseData
{
    public TeamDto Team { get; set; }
}
