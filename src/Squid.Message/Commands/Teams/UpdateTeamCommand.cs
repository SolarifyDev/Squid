using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Models.Teams;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

[RequiresPermission(Permission.TeamEdit)]
public class UpdateTeamCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    // P0-D.2: converted from int to int? so SpaceIdInjectionSpecification can inject
    // the header value. Previously a body-supplied SpaceId would pass the permission
    // check even if the caller only had TeamEdit in a different space.
    public int? SpaceId { get; set; }
}

public class UpdateTeamResponse : SquidResponse<TeamDto>
{
}
