using Squid.Core.Services.Teams;
using Squid.Message.Commands.Teams;

namespace Squid.Core.Handlers.CommandHandlers.Teams;

public class DeleteTeamCommandHandler(ITeamService teamService) : ICommandHandler<DeleteTeamCommand, DeleteTeamResponse>
{
    public async Task<DeleteTeamResponse> Handle(IReceiveContext<DeleteTeamCommand> context, CancellationToken cancellationToken)
    {
        await teamService.DeleteAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new DeleteTeamResponse();
    }
}
