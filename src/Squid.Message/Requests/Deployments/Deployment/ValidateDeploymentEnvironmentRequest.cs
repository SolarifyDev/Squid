namespace Squid.Message.Requests.Deployments.Deployment;

public class ValidateDeploymentEnvironmentRequest : IRequest
{
    public int ReleaseId { get; set; }

    public int EnvironmentId { get; set; }
}

public class ValidateDeploymentEnvironmentResponse : Models.Deployments.Deployment.DeploymentEnvironmentValidationResult, IResponse;
