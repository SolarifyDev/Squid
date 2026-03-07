using Squid.Core.Services.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Release;

public class GetReleaseVariableSnapshotRequestHandler : IRequestHandler<GetReleaseVariableSnapshotRequest, GetReleaseVariableSnapshotResponse>
{
    private readonly IReleaseService _releaseService;

    public GetReleaseVariableSnapshotRequestHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task<GetReleaseVariableSnapshotResponse> Handle(IReceiveContext<GetReleaseVariableSnapshotRequest> context, CancellationToken cancellationToken)
    {
        return await _releaseService.GetReleaseVariableSnapshotAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
