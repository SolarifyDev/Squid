using Squid.Core.Services.Account;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Account;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class ChangePasswordCommandHandler(IAccountService accountService, ICurrentUser currentUser) : ICommandHandler<ChangePasswordCommand, ChangePasswordResponse>
{
    public async Task<ChangePasswordResponse> Handle(IReceiveContext<ChangePasswordCommand> context, CancellationToken cancellationToken)
    {
        var userId = context.Message.UserId;

        if (currentUser.Id != userId)
            throw new UnauthorizedAccessException("Can only change your own password");

        await accountService.ChangePasswordAsync(userId, context.Message.CurrentPassword, context.Message.NewPassword, cancellationToken).ConfigureAwait(false);

        return new ChangePasswordResponse();
    }
}
