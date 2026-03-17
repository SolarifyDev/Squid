using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;

namespace Squid.Core.Handlers.CommandHandlers.Teams;

public class AddTeamMemberCommandHandler(ITeamService teamService) : ICommandHandler<AddTeamMemberCommand, AddTeamMemberResponse>
{
    public async Task<AddTeamMemberResponse> Handle(IReceiveContext<AddTeamMemberCommand> context, CancellationToken cancellationToken)
    {
        await teamService.AddMemberAsync(context.Message.TeamId, context.Message.UserId, cancellationToken).ConfigureAwait(false);

        return new AddTeamMemberResponse();
    }
}
