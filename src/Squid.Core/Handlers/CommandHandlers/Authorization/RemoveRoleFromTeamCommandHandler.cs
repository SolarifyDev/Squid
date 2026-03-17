using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.Core.Handlers.CommandHandlers.Authorization;

public class RemoveRoleFromTeamCommandHandler(IUserRoleService userRoleService) : ICommandHandler<RemoveRoleFromTeamCommand, RemoveRoleFromTeamResponse>
{
    public async Task<RemoveRoleFromTeamResponse> Handle(IReceiveContext<RemoveRoleFromTeamCommand> context, CancellationToken cancellationToken)
    {
        await userRoleService.RemoveRoleFromTeamAsync(context.Message.ScopedUserRoleId, cancellationToken).ConfigureAwait(false);

        return new RemoveRoleFromTeamResponse();
    }
}
