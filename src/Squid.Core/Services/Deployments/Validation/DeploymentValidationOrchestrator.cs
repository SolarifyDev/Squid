namespace Squid.Core.Services.Deployments.Validation;

public interface IDeploymentValidationOrchestrator : IScopedDependency
{
    Task<DeploymentValidationReport> ValidateAsync(DeploymentValidationStage stage, DeploymentValidationContext context, CancellationToken cancellationToken = default);
}

public class DeploymentValidationOrchestrator : IDeploymentValidationOrchestrator
{
    private readonly IReadOnlyList<IDeploymentValidationRule> _rules;

    public DeploymentValidationOrchestrator(IEnumerable<IDeploymentValidationRule> rules)
    {
        _rules = rules.OrderBy(r => r.Order).ToList();
    }

    public async Task<DeploymentValidationReport> ValidateAsync(DeploymentValidationStage stage, DeploymentValidationContext context, CancellationToken cancellationToken = default)
    {
        var report = new DeploymentValidationReport();

        foreach (var rule in _rules.Where(r => r.Supports(stage)))
        {
            await rule.EvaluateAsync(context, report, cancellationToken).ConfigureAwait(false);
        }

        return report;
    }
}
