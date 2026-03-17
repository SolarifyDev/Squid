using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.Core.Handlers.CommandHandlers.Authorization;

public class UpdateRoleScopeCommandHandler(IUserRoleService userRoleService) : ICommandHandler<UpdateRoleScopeCommand, UpdateRoleScopeResponse>
{
    public async Task<UpdateRoleScopeResponse> Handle(IReceiveContext<UpdateRoleScopeCommand> context, CancellationToken cancellationToken)
    {
        var scopedRole = await userRoleService.UpdateRoleScopeAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateRoleScopeResponse { Data = scopedRole };
    }
}
