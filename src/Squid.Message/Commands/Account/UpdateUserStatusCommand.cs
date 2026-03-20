using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Account;

[RequiresPermission(Permission.UserEdit)]
public class UpdateUserStatusCommand : ICommand
{
    public int UserId { get; set; }
    public bool IsDisabled { get; set; }
}

public class UpdateUserStatusResponse : SquidResponse
{
}
