using Squid.Core.Services.Authorization;
using Squid.Message.Requests.Authorization;

namespace Squid.Core.Handlers.RequestHandlers.Authorization;

public class GetUserRoleRequestHandler(IUserRoleService userRoleService) : IRequestHandler<GetUserRoleRequest, GetUserRoleResponse>
{
    public async Task<GetUserRoleResponse> Handle(IReceiveContext<GetUserRoleRequest> context, CancellationToken cancellationToken)
    {
        var role = await userRoleService.GetByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new GetUserRoleResponse { Data = role };
    }
}
