using Squid.Message.Response;

namespace Squid.Message.Commands.Account;

public class ChangePasswordCommand : ICommand
{
    public int UserId { get; set; }
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; }
}

public class ChangePasswordResponse : SquidResponse
{
}
