using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.ExternalFeed;

public class SearchFeedPackageVersionsRequestHandler(IExternalFeedPackageVersionService packageVersionService) : IRequestHandler<SearchFeedPackageVersionsRequest, SearchFeedPackageVersionsResponse>
{
    public async Task<SearchFeedPackageVersionsResponse> Handle(IReceiveContext<SearchFeedPackageVersionsRequest> context, CancellationToken cancellationToken)
    {
        var msg = context.Message;

        var result = await packageVersionService
            .ListVersionsAsync(msg.FeedId, msg.PackageId, msg.Take, msg.IncludePreRelease, msg.Filter, cancellationToken).ConfigureAwait(false);

        return new SearchFeedPackageVersionsResponse { Data = result };
    }
}
