using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.Core.Handlers.CommandHandlers.Authorization;

public class AssignRoleToTeamCommandHandler(IUserRoleService userRoleService) : ICommandHandler<AssignRoleToTeamCommand, AssignRoleToTeamResponse>
{
    public async Task<AssignRoleToTeamResponse> Handle(IReceiveContext<AssignRoleToTeamCommand> context, CancellationToken cancellationToken)
    {
        var scopedRole = await userRoleService.AssignRoleToTeamAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new AssignRoleToTeamResponse { Data = scopedRole };
    }
}
