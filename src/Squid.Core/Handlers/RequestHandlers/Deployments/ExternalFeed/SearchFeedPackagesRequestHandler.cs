using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.ExternalFeed;

public class SearchFeedPackagesRequestHandler(IExternalFeedPackageSearchService packageSearchService) : IRequestHandler<SearchFeedPackagesRequest, SearchFeedPackagesResponse>
{
    public async Task<SearchFeedPackagesResponse> Handle(IReceiveContext<SearchFeedPackagesRequest> context, CancellationToken cancellationToken)
    {
        var result = await packageSearchService
            .SearchAsync(context.Message.FeedId, context.Message.Query, context.Message.Take, cancellationToken).ConfigureAwait(false);

        return new SearchFeedPackagesResponse { Data = result };
    }
}
