using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

[RequiresPermission(Permission.TeamEdit)]
public class AddTeamMemberCommand : ICommand, ISpaceScoped
{
    public int TeamId { get; set; }
    public int UserId { get; set; }

    // P0-D.2: injected by SpaceIdInjectionSpecification from X-Space-Id header.
    public int? SpaceId { get; set; }
}

public class AddTeamMemberResponse : SquidResponse
{
}
