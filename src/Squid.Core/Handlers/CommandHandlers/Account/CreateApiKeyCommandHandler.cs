using Squid.Core.Services.Account;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Account;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class CreateApiKeyCommandHandler(IAccountService accountService, ICurrentUser currentUser) : ICommandHandler<CreateApiKeyCommand, CreateApiKeyResponse>
{
    public async Task<CreateApiKeyResponse> Handle(IReceiveContext<CreateApiKeyCommand> context, CancellationToken cancellationToken)
    {
        var result = await accountService.CreateApiKeyAsync(currentUser.Id!.Value, context.Message.Description, cancellationToken).ConfigureAwait(false);

        return new CreateApiKeyResponse { Data = result };
    }
}
