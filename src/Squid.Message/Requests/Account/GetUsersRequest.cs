using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Account;
using Squid.Message.Response;

namespace Squid.Message.Requests.Account;

[RequiresPermission(Permission.UserView)]
public class GetUsersRequest : IRequest
{
}

public class GetUsersResponse : SquidResponse<List<UserAccountDto>>
{
}
