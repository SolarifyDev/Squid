using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Requests.Authorization;

[RequiresPermission(Permission.UserRoleView)]
public class GetUserRoleRequest : IRequest
{
    public int Id { get; set; }
}

public class GetUserRoleResponse : SquidResponse<UserRoleDto>
{
}
