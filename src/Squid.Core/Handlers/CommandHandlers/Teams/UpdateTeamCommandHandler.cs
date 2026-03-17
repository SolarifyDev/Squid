using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;

namespace Squid.Core.Handlers.CommandHandlers.Teams;

public class UpdateTeamCommandHandler(ITeamService teamService) : ICommandHandler<UpdateTeamCommand, UpdateTeamResponse>
{
    public async Task<UpdateTeamResponse> Handle(IReceiveContext<UpdateTeamCommand> context, CancellationToken cancellationToken)
    {
        var team = await teamService.UpdateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateTeamResponse { Data = new UpdateTeamResponseData { Team = team } };
    }
}
