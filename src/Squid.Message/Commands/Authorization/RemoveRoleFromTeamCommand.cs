using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.TeamEdit)]
public class RemoveRoleFromTeamCommand : ICommand, ISpaceScoped
{
    public int TeamId { get; set; }
    public int ScopedUserRoleId { get; set; }

    // P0-D.2: injected by SpaceIdInjectionSpecification from the X-Space-Id header.
    public int? SpaceId { get; set; }
}

public class RemoveRoleFromTeamResponse : SquidResponse
{
}
