using Squid.Core.Services.Authorization;
using Squid.Message.Requests.Authorization;

namespace Squid.Core.Handlers.RequestHandlers.Authorization;

public class GetPermissionsRequestHandler(IUserRoleService userRoleService) : IRequestHandler<GetPermissionsRequest, GetPermissionsResponse>
{
    public async Task<GetPermissionsResponse> Handle(IReceiveContext<GetPermissionsRequest> context, CancellationToken cancellationToken)
    {
        var permissions = await userRoleService.GetAllPermissionsAsync().ConfigureAwait(false);

        return new GetPermissionsResponse { Data = permissions };
    }
}
