using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.UserRoleEdit)]
public class DeleteUserRoleCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }

    // P0-D.2: see CreateUserRoleCommand — same rationale.
    public int? SpaceId { get; set; }
}

public class DeleteUserRoleResponse : SquidResponse
{
}
