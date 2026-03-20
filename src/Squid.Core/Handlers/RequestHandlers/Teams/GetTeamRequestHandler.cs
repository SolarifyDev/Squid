using Squid.Core.Services.Teams;
using Squid.Message.Requests.Teams;

namespace Squid.Core.Handlers.RequestHandlers.Teams;

public class GetTeamRequestHandler(ITeamService teamService) : IRequestHandler<GetTeamRequest, GetTeamResponse>
{
    public async Task<GetTeamResponse> Handle(IReceiveContext<GetTeamRequest> context, CancellationToken cancellationToken)
    {
        var team = await teamService.GetByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new GetTeamResponse { Data = team };
    }
}
