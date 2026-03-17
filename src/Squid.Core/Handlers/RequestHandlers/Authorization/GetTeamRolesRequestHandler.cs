using Squid.Core.Services.Authorization;
using Squid.Message.Requests.Authorization;

namespace Squid.Core.Handlers.RequestHandlers.Authorization;

public class GetTeamRolesRequestHandler(IUserRoleService userRoleService) : IRequestHandler<GetTeamRolesRequest, GetTeamRolesResponse>
{
    public async Task<GetTeamRolesResponse> Handle(IReceiveContext<GetTeamRolesRequest> context, CancellationToken cancellationToken)
    {
        var scopedRoles = await userRoleService.GetTeamRolesAsync(context.Message.TeamId, cancellationToken).ConfigureAwait(false);

        return new GetTeamRolesResponse { Data = scopedRoles };
    }
}
