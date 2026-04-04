using Squid.Core.Services.Deployments.Release;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Release;

public class GetReleaseDetailRequestHandler(IReleaseDataProvider releaseDataProvider, IReleaseSelectedPackageDataProvider selectedPackageDataProvider)
    : IRequestHandler<GetReleaseDetailRequest, GetReleaseDetailResponse>
{
    public async Task<GetReleaseDetailResponse> Handle(IReceiveContext<GetReleaseDetailRequest> context, CancellationToken cancellationToken)
    {
        var release = await releaseDataProvider.GetReleaseByIdAsync(context.Message.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
            throw new InvalidOperationException($"Release {context.Message.ReleaseId} not found");

        var packages = await selectedPackageDataProvider.GetByReleaseIdAsync(release.Id, cancellationToken).ConfigureAwait(false);

        return new GetReleaseDetailResponse
        {
            Data = new ReleaseDetailDto
            {
                Id = release.Id,
                Version = release.Version,
                ProjectId = release.ProjectId,
                ChannelId = release.ChannelId,
                SpaceId = release.SpaceId,
                CreatedDate = release.CreatedDate,
                LastModifiedDate = release.LastModifiedDate,
                SelectedPackages = packages.Select(p => new SelectedPackageDto
                {
                    ActionName = p.ActionName,
                    PackageReferenceName = p.PackageReferenceName,
                    Version = p.Version
                }).ToList()
            }
        };
    }
}
