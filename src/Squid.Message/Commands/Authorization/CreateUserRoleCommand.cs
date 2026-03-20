using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.UserRoleEdit)]
public class CreateUserRoleCommand : ICommand
{
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> Permissions { get; set; } = new();
}

public class CreateUserRoleResponse : SquidResponse<UserRoleDto>
{
}
