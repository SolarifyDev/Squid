using Squid.Core.Services.Spaces;
using Squid.Message.Requests.Spaces;

namespace Squid.Core.Handlers.RequestHandlers.Spaces;

public class GetSpaceRequestHandler(ISpaceService spaceService) : IRequestHandler<GetSpaceRequest, GetSpaceResponse>
{
    public async Task<GetSpaceResponse> Handle(IReceiveContext<GetSpaceRequest> context, CancellationToken cancellationToken)
    {
        var space = await spaceService.GetByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new GetSpaceResponse { Data = space };
    }
}
