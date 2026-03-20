using Squid.Core.Services.Account;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Account;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class UpdateUserStatusCommandHandler(IAccountService accountService, ICurrentUser currentUser) : ICommandHandler<UpdateUserStatusCommand, UpdateUserStatusResponse>
{
    public async Task<UpdateUserStatusResponse> Handle(IReceiveContext<UpdateUserStatusCommand> context, CancellationToken cancellationToken)
    {
        await accountService.UpdateUserStatusAsync(context.Message.UserId, context.Message.IsDisabled, currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);

        return new UpdateUserStatusResponse();
    }
}
