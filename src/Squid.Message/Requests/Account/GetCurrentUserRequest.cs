using Squid.Message.Models.Account;
using Squid.Message.Response;

namespace Squid.Message.Requests.Account;

public class GetCurrentUserRequest : IRequest
{
    public int UserId { get; set; }
}

public class GetCurrentUserResponse : SquidResponse<UserAccountDto?>
{
}
