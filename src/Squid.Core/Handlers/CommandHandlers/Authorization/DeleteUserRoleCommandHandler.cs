using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.Core.Handlers.CommandHandlers.Authorization;

public class DeleteUserRoleCommandHandler(IUserRoleService userRoleService) : ICommandHandler<DeleteUserRoleCommand, DeleteUserRoleResponse>
{
    public async Task<DeleteUserRoleResponse> Handle(IReceiveContext<DeleteUserRoleCommand> context, CancellationToken cancellationToken)
    {
        await userRoleService.DeleteAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new DeleteUserRoleResponse();
    }
}
