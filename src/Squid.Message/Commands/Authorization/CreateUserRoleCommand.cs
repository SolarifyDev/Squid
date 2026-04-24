using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.UserRoleEdit)]
public class CreateUserRoleCommand : ICommand, ISpaceScoped
{
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> Permissions { get; set; } = new();

    // P0-D.2: ISpaceScoped is declared for consistency with the other authorization
    // commands even though UserRoleEdit is PermissionScope.SystemOnly (and therefore
    // already gated at system level by AuthorizationService.FilterBySpace).
    public int? SpaceId { get; set; }
}

public class CreateUserRoleResponse : SquidResponse<UserRoleDto>
{
}
