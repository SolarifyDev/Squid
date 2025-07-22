using Squid.Core.Services.Deployments.ExternalFeed;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.ExternalFeed;

public class GetExternalFeedsRequestHandler : IRequestHandler<GetExternalFeedsRequest, GetExternalFeedsResponse>
{
    private readonly IExternalFeedService _externalFeedService;

    public GetExternalFeedsRequestHandler(IExternalFeedService externalFeedService)
    {
        _externalFeedService = externalFeedService;
    }

    public async Task<GetExternalFeedsResponse> Handle(IReceiveContext<GetExternalFeedsRequest> context, CancellationToken cancellationToken)
    {
        return await _externalFeedService.GetExternalFeedsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
