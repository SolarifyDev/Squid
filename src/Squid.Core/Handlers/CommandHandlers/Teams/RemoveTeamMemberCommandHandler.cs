using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;

namespace Squid.Core.Handlers.CommandHandlers.Teams;

public class RemoveTeamMemberCommandHandler(ITeamService teamService) : ICommandHandler<RemoveTeamMemberCommand, RemoveTeamMemberResponse>
{
    public async Task<RemoveTeamMemberResponse> Handle(IReceiveContext<RemoveTeamMemberCommand> context, CancellationToken cancellationToken)
    {
        await teamService.RemoveMemberAsync(context.Message.TeamId, context.Message.UserId, cancellationToken).ConfigureAwait(false);

        return new RemoveTeamMemberResponse();
    }
}
