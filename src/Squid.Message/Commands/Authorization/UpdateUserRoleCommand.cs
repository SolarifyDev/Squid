using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.UserRoleEdit)]
public class UpdateUserRoleCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> Permissions { get; set; } = new();

    // P0-D.2: see CreateUserRoleCommand — same rationale.
    public int? SpaceId { get; set; }
}

public class UpdateUserRoleResponse : SquidResponse<UserRoleDto>
{
}
