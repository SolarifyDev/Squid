using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.TeamEdit)]
public class UpdateRoleScopeCommand : ICommand, ISpaceScoped
{
    public int TeamId { get; set; }
    public int ScopedUserRoleId { get; set; }
    public List<int> ProjectIds { get; set; } = new();
    public List<int> EnvironmentIds { get; set; } = new();
    public List<int> ProjectGroupIds { get; set; } = new();

    // P0-D.2: Populated by SpaceIdInjectionSpecification from the X-Space-Id header so
    // cross-space scope edits can't bypass the TeamEdit space check.
    public int? SpaceId { get; set; }
}

public class UpdateRoleScopeResponse : SquidResponse<ScopedUserRoleDto>
{
}
