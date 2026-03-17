using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Message.Requests.Authorization;

namespace Squid.Core.Handlers.RequestHandlers.Authorization;

public class GetMyPermissionsRequestHandler(IUserRoleService userRoleService, ICurrentUser currentUser) : IRequestHandler<GetMyPermissionsRequest, GetMyPermissionsResponse>
{
    public async Task<GetMyPermissionsResponse> Handle(IReceiveContext<GetMyPermissionsRequest> context, CancellationToken cancellationToken)
    {
        var userId = currentUser.Id ?? 0;
        var permissionSet = await userRoleService.GetUserPermissionsAsync(userId, cancellationToken).ConfigureAwait(false);

        return new GetMyPermissionsResponse { Data = permissionSet };
    }
}
