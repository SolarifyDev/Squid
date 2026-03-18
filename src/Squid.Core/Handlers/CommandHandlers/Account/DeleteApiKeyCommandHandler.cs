using Squid.Core.Services.Account;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Account;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class DeleteApiKeyCommandHandler(IAccountService accountService, ICurrentUser currentUser) : ICommandHandler<DeleteApiKeyCommand, DeleteApiKeyResponse>
{
    public async Task<DeleteApiKeyResponse> Handle(IReceiveContext<DeleteApiKeyCommand> context, CancellationToken cancellationToken)
    {
        await accountService.DeleteApiKeyAsync(context.Message.ApiKeyId, currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);

        return new DeleteApiKeyResponse();
    }
}
