using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

[RequiresPermission(Permission.TeamDelete)]
public class DeleteTeamCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }

    // P0-D.2: injected by SpaceIdInjectionSpecification from X-Space-Id header.
    public int? SpaceId { get; set; }
}

public class DeleteTeamResponse : SquidResponse
{
}
