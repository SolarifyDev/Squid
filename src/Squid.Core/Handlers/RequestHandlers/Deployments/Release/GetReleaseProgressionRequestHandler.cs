using Squid.Core.Services.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Release;

public class GetReleaseProgressionRequestHandler : IRequestHandler<GetReleaseProgressionRequest, GetReleaseProgressionResponse>
{
    private readonly IReleaseService _releaseService;

    public GetReleaseProgressionRequestHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task<GetReleaseProgressionResponse> Handle(IReceiveContext<GetReleaseProgressionRequest> context, CancellationToken cancellationToken)
    {
        return await _releaseService.GetReleaseProgressionAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
