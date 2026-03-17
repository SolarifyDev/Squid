using Squid.Core.Services.Account;
using Squid.Message.Commands.Account;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class CreateUserCommandHandler : ICommandHandler<CreateUserCommand, CreateUserResponse>
{
    private readonly IAccountService _accountService;

    public CreateUserCommandHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public async Task<CreateUserResponse> Handle(IReceiveContext<CreateUserCommand> context, CancellationToken cancellationToken)
    {
        var result = await _accountService.CreateUserAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateUserResponse
        {
            Data = result
        };
    }
}
