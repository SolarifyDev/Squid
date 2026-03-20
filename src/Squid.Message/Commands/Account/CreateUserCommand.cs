using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Account;
using Squid.Message.Response;

namespace Squid.Message.Commands.Account;

[RequiresPermission(Permission.UserEdit)]
public class CreateUserCommand : ICommand
{
    public string UserName { get; set; }

    public string Password { get; set; }

    public string? DisplayName { get; set; }
}

public class CreateUserResponse : SquidResponse<CreateUserResponseData>
{
}

public class CreateUserResponseData
{
    public bool IsSucceeded { get; set; }

    public UserAccountDto UserAccount { get; set; }
}
