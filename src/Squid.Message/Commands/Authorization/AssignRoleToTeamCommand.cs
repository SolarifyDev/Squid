using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.TeamEdit)]
public class AssignRoleToTeamCommand : ICommand, ISpaceScoped
{
    public int TeamId { get; set; }
    public int UserRoleId { get; set; }

    // P0-D.2: ISpaceScoped required so SpaceIdInjectionSpecification injects the header
    // value. Without this, a TeamEdit holder in any space could body-supply SpaceId and
    // pass the permission check in a space they don't actually have TeamEdit in.
    public int? SpaceId { get; set; }
}

public class AssignRoleToTeamResponse : SquidResponse<ScopedUserRoleDto>
{
}
