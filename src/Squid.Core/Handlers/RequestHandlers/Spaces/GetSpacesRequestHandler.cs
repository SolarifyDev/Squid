using Squid.Core.Services.Spaces;
using Squid.Message.Requests.Spaces;

namespace Squid.Core.Handlers.RequestHandlers.Spaces;

public class GetSpacesRequestHandler(ISpaceService spaceService) : IRequestHandler<GetSpacesRequest, GetSpacesResponse>
{
    public async Task<GetSpacesResponse> Handle(IReceiveContext<GetSpacesRequest> context, CancellationToken cancellationToken)
    {
        var spaces = await spaceService.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new GetSpacesResponse { Data = spaces };
    }
}
