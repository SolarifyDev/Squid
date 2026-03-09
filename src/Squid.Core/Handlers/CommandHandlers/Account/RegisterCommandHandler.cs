using Squid.Core.Services.Account;
using Squid.Message.Commands.Account;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class RegisterCommandHandler : ICommandHandler<RegisterCommand, RegisterResponse>
{
    private readonly IAccountService _accountService;

    public RegisterCommandHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public async Task<RegisterResponse> Handle(IReceiveContext<RegisterCommand> context, CancellationToken cancellationToken)
    {
        var result = await _accountService.RegisterAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterResponse
        {
            Data = result
        };
    }
}
