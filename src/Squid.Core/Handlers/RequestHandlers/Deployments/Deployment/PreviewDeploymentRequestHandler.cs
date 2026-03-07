using Squid.Core.Services.Deployments.Deployments;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;

public class PreviewDeploymentRequestHandler : IRequestHandler<PreviewDeploymentRequest, PreviewDeploymentResponse>
{
    private readonly IDeploymentService _deploymentService;

    public PreviewDeploymentRequestHandler(IDeploymentService deploymentService)
    {
        _deploymentService = deploymentService;
    }

    public async Task<PreviewDeploymentResponse> Handle(IReceiveContext<PreviewDeploymentRequest> context, CancellationToken cancellationToken)
    {
        var preview = await _deploymentService
            .PreviewDeploymentAsync(context.Message.DeploymentRequestPayload, cancellationToken).ConfigureAwait(false);

        return new PreviewDeploymentResponse
        {
            CanDeploy = preview.CanDeploy,
            BlockingReasons = preview.BlockingReasons,
            AvailableMachineCount = preview.AvailableMachineCount,
            LifecycleId = preview.LifecycleId,
            AllowedEnvironmentIds = preview.AllowedEnvironmentIds,
            CandidateTargets = preview.CandidateTargets,
            Steps = preview.Steps
        };
    }
}
