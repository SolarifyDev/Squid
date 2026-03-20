using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Requests.Authorization;

[RequiresPermission(Permission.UserRoleView)]
public class GetUserRolesRequest : IRequest
{
}

public class GetUserRolesResponse : SquidResponse<List<UserRoleDto>>
{
}
