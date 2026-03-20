using Squid.Core.Services.Authorization;
using Squid.Message.Requests.Authorization;

namespace Squid.Core.Handlers.RequestHandlers.Authorization;

public class GetUserRolesRequestHandler(IUserRoleService userRoleService) : IRequestHandler<GetUserRolesRequest, GetUserRolesResponse>
{
    public async Task<GetUserRolesResponse> Handle(IReceiveContext<GetUserRolesRequest> context, CancellationToken cancellationToken)
    {
        var roles = await userRoleService.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new GetUserRolesResponse { Data = roles };
    }
}
