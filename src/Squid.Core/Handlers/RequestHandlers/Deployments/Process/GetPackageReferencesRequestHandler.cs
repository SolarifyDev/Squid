using Squid.Core.Services.Deployments.Process;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Process;

public class GetPackageReferencesRequestHandler : IRequestHandler<GetPackageReferencesRequest, GetPackageReferencesResponse>
{
    private readonly IDeploymentPackageReferenceService _packageReferenceService;

    public GetPackageReferencesRequestHandler(IDeploymentPackageReferenceService packageReferenceService)
    {
        _packageReferenceService = packageReferenceService;
    }

    public async Task<GetPackageReferencesResponse> Handle(IReceiveContext<GetPackageReferencesRequest> context, CancellationToken cancellationToken)
    {
        var references = await _packageReferenceService
            .GetPackageReferencesAsync(context.Message.ProjectId, cancellationToken).ConfigureAwait(false);

        return new GetPackageReferencesResponse
        {
            Data = new GetPackageReferencesResponseData
            {
                PackageReferences = references.Select(r => new PackageReferenceItem
                {
                    ActionName = r.ActionName,
                    PackageReferenceName = r.PackageReferenceName,
                    PackageId = r.PackageId,
                    FeedId = r.FeedId,
                    FeedName = r.FeedName,
                    LastReleaseVersion = r.LastReleaseVersion
                }).ToList()
            }
        };
    }
}
