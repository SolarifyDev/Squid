using Squid.Core.Services.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Release;

public class GetReleasesRequestHandler : IRequestHandler<GetReleasesRequest, GetReleasesResponse>
{
    private readonly IReleaseService _releaseService;

    public GetReleasesRequestHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task<GetReleasesResponse> Handle(IReceiveContext<GetReleasesRequest> context, CancellationToken cancellationToken)
    {
        return await _releaseService.GetReleasesAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}