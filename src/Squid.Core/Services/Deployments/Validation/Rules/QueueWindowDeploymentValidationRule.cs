namespace Squid.Core.Services.Deployments.Validation.Rules;

public sealed class QueueWindowDeploymentValidationRule : IDeploymentValidationRule
{
    private static readonly TimeSpan MinimumQueueLeadTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumQueueLeadTime = TimeSpan.FromDays(30);

    public int Order => 200;

    public bool Supports(DeploymentValidationStage stage) => stage == DeploymentValidationStage.Precheck || stage == DeploymentValidationStage.Create;

    public Task EvaluateAsync(DeploymentValidationContext context, DeploymentValidationReport report, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (!context.QueueTime.HasValue && context.QueueTimeExpiry.HasValue)
        {
            report.AddBlockingIssue(DeploymentValidationIssueCode.QueueTimeExpiryBeforeQueueTime, "QueueTimeExpiry requires QueueTime.");
            
            return Task.CompletedTask;
        }

        if (context.QueueTime.HasValue)
        {
            var delta = context.QueueTime.Value - now;

            if (delta < MinimumQueueLeadTime)
            {
                report.AddBlockingIssue(DeploymentValidationIssueCode.QueueTimeTooSoon, "QueueTime must be at least 1 minute in the future.");
            }

            if (delta > MaximumQueueLeadTime)
            {
                report.AddBlockingIssue(DeploymentValidationIssueCode.QueueTimeTooFar, "QueueTime cannot be more than 30 days in the future.");
            }
        }

        if (context.QueueTime.HasValue &&
            context.QueueTimeExpiry.HasValue &&
            context.QueueTimeExpiry.Value <= context.QueueTime.Value)
        {
            report.AddBlockingIssue(DeploymentValidationIssueCode.QueueTimeExpiryBeforeQueueTime, "QueueTimeExpiry must be later than QueueTime.");
        }

        return Task.CompletedTask;
    }
}
