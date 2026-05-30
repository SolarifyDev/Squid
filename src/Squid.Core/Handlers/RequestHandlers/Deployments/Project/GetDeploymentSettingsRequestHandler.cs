using Squid.Core.Services.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Project;

public class GetDeploymentSettingsRequestHandler(IProjectDeploymentSettingsService deploymentSettingsService)
    : IRequestHandler<GetDeploymentSettingsRequest, GetDeploymentSettingsResponse>
{
    public async Task<GetDeploymentSettingsResponse> Handle(IReceiveContext<GetDeploymentSettingsRequest> context, CancellationToken cancellationToken)
    {
        var settings = await deploymentSettingsService.GetAsync(context.Message.ProjectId, cancellationToken).ConfigureAwait(false);

        return new GetDeploymentSettingsResponse { Data = settings };
    }
}
