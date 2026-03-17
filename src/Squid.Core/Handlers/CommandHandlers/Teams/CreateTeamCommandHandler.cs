using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;

namespace Squid.Core.Handlers.CommandHandlers.Teams;

public class CreateTeamCommandHandler(ITeamService teamService) : ICommandHandler<CreateTeamCommand, CreateTeamResponse>
{
    public async Task<CreateTeamResponse> Handle(IReceiveContext<CreateTeamCommand> context, CancellationToken cancellationToken)
    {
        var team = await teamService.CreateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateTeamResponse { Data = new CreateTeamResponseData { Team = team } };
    }
}
