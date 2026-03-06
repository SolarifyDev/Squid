using Squid.Core.Services.Deployments.Deployments;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;

public class ValidateDeploymentEnvironmentRequestHandler : IRequestHandler<ValidateDeploymentEnvironmentRequest, ValidateDeploymentEnvironmentResponse>
{
    private readonly IDeploymentValidationService _deploymentValidationService;

    public ValidateDeploymentEnvironmentRequestHandler(IDeploymentValidationService deploymentValidationService)
    {
        _deploymentValidationService = deploymentValidationService;
    }

    public async Task<ValidateDeploymentEnvironmentResponse> Handle(IReceiveContext<ValidateDeploymentEnvironmentRequest> context, CancellationToken cancellationToken)
    {
        var validation = await _deploymentValidationService
            .ValidateDeploymentEnvironmentDetailedAsync(context.Message.ReleaseId, context.Message.EnvironmentId, cancellationToken).ConfigureAwait(false);

        return new ValidateDeploymentEnvironmentResponse
        {
            IsValid = validation.IsValid,
            Reasons = validation.Reasons.ToList(),
            AvailableMachineCount = validation.AvailableMachineCount,
            LifecycleId = validation.LifecycleId,
            AllowedEnvironmentIds = validation.AllowedEnvironmentIds.ToList()
        };
    }
}
