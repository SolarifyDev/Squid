namespace Squid.Core.Services.Deployments.Validation.Rules;

public sealed class MachineSelectionConsistencyValidationRule : IDeploymentValidationRule
{
    public int Order => 100;

    public bool Supports(DeploymentValidationStage stage) => stage == DeploymentValidationStage.Precheck || stage == DeploymentValidationStage.Create;

    public Task EvaluateAsync(DeploymentValidationContext context, DeploymentValidationReport report, CancellationToken cancellationToken = default)
    {
        if (context.SpecificMachineIds.Overlaps(context.ExcludedMachineIds))
        {
            report.AddBlockingIssue(DeploymentValidationIssueCode.MachineSelectionOverlap, "SpecificMachineIds and ExcludedMachineIds cannot overlap.");
        }

        return Task.CompletedTask;
    }
}
