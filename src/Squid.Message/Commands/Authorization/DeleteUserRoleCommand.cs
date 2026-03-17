using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.UserRoleEdit)]
public class DeleteUserRoleCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteUserRoleResponse : SquidResponse
{
}
