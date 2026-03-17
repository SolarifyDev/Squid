using Squid.Core.Services.Teams;
using Squid.Message.Requests.Teams;

namespace Squid.Core.Handlers.RequestHandlers.Teams;

public class GetTeamMembersRequestHandler(ITeamService teamService) : IRequestHandler<GetTeamMembersRequest, GetTeamMembersResponse>
{
    public async Task<GetTeamMembersResponse> Handle(IReceiveContext<GetTeamMembersRequest> context, CancellationToken cancellationToken)
    {
        var members = await teamService.GetMembersAsync(context.Message.TeamId, cancellationToken).ConfigureAwait(false);

        return new GetTeamMembersResponse { Data = new GetTeamMembersResponseData { Members = members } };
    }
}
