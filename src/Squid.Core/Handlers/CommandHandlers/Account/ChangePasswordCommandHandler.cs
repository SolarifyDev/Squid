using Squid.Core.Services.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Account;
using Squid.Message.Enums;

namespace Squid.Core.Handlers.CommandHandlers.Account;

public class ChangePasswordCommandHandler(IAccountService accountService, ICurrentUser currentUser, IAuthorizationService authorizationService) : ICommandHandler<ChangePasswordCommand, ChangePasswordResponse>
{
    public async Task<ChangePasswordResponse> Handle(IReceiveContext<ChangePasswordCommand> context, CancellationToken cancellationToken)
    {
        var userId = context.Message.UserId;
        var isSelf = currentUser.Id == userId;

        if (!isSelf)
        {
            await authorizationService.EnsurePermissionAsync(
                new PermissionCheckRequest { UserId = currentUser.Id!.Value, Permission = Permission.UserEdit }, cancellationToken).ConfigureAwait(false);
        }

        await accountService.ChangePasswordAsync(userId, context.Message.CurrentPassword, context.Message.NewPassword, isSelf, cancellationToken).ConfigureAwait(false);

        return new ChangePasswordResponse();
    }
}
