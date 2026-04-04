using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.ExternalFeed;

public class GetPackageNotesRequestHandler(IExternalFeedPackageNotesService packageNotesService)
    : IRequestHandler<GetPackageNotesRequest, GetPackageNotesResponse>
{
    public async Task<GetPackageNotesResponse> Handle(IReceiveContext<GetPackageNotesRequest> context, CancellationToken cancellationToken)
    {
        var result = await packageNotesService
            .GetNotesAsync(context.Message.Packages, cancellationToken).ConfigureAwait(false);

        return new GetPackageNotesResponse { Data = result };
    }
}
