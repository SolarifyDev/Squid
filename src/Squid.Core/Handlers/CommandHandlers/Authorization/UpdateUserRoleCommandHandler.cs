using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.Core.Handlers.CommandHandlers.Authorization;

public class UpdateUserRoleCommandHandler(IUserRoleService userRoleService) : ICommandHandler<UpdateUserRoleCommand, UpdateUserRoleResponse>
{
    public async Task<UpdateUserRoleResponse> Handle(IReceiveContext<UpdateUserRoleCommand> context, CancellationToken cancellationToken)
    {
        var role = await userRoleService.UpdateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateUserRoleResponse { Data = role };
    }
}
