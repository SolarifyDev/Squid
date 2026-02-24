using Squid.Message.Models.Account;
using Squid.Message.Response;

namespace Squid.Message.Commands.Account;

public class RegisterCommand : ICommand
{
    public string UserName { get; set; }

    public string Password { get; set; }

    public string? DisplayName { get; set; }
}

public class RegisterResponse : SquidResponse<RegisterResponseData>
{
}

public class RegisterResponseData
{
    public bool IsSucceeded { get; set; }

    public UserAccountDto UserAccount { get; set; }
}
