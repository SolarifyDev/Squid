using Squid.Core.Services.Spaces;
using Squid.Message.Requests.Spaces;

namespace Squid.Core.Handlers.RequestHandlers.Spaces;

public class GetSpaceManagersRequestHandler(ISpaceService spaceService) : IRequestHandler<GetSpaceManagersRequest, GetSpaceManagersResponse>
{
    public async Task<GetSpaceManagersResponse> Handle(IReceiveContext<GetSpaceManagersRequest> context, CancellationToken cancellationToken)
    {
        var managers = await spaceService.GetManagerTeamsAsync(context.Message.SpaceId, cancellationToken).ConfigureAwait(false);

        return new GetSpaceManagersResponse { Data = managers };
    }
}
