using Squid.Core.Services.Teams;
using Squid.Message.Requests.Teams;

namespace Squid.Core.Handlers.RequestHandlers.Teams;

public class GetTeamsRequestHandler(ITeamService teamService) : IRequestHandler<GetTeamsRequest, GetTeamsResponse>
{
    public async Task<GetTeamsResponse> Handle(IReceiveContext<GetTeamsRequest> context, CancellationToken cancellationToken)
    {
        var teams = await teamService.GetAllBySpaceAsync(context.Message.SpaceId, cancellationToken).ConfigureAwait(false);

        return new GetTeamsResponse { Data = teams };
    }
}
