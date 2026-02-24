using Squid.Message.Models.Account;
using Squid.Message.Response;

namespace Squid.Message.Requests.Account;

public class LoginRequest : IRequest
{
    public string UserName { get; set; }

    public string Password { get; set; }
}

public class LoginResponse : SquidResponse<LoginResponseData>
{
}

public class LoginResponseData
{
    public string AccessToken { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public UserAccountDto UserAccount { get; set; }
}
