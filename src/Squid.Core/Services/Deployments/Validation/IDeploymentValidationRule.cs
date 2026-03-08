namespace Squid.Core.Services.Deployments.Validation;

public interface IDeploymentValidationRule : IScopedDependency
{
    int Order { get; }

    bool Supports(DeploymentValidationStage stage);

    Task EvaluateAsync(DeploymentValidationContext context, DeploymentValidationReport report, CancellationToken cancellationToken = default);
}
