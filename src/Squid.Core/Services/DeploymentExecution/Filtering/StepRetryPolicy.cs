using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public readonly record struct StepRetryPolicy(bool Enabled, int MaxAttempts)
{
    public static StepRetryPolicy FromStep(DeploymentStepDto step)
    {
        var enabled = string.Equals(
            step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.RetriesEnabled)?.PropertyValue,
            "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled) return new StepRetryPolicy(false, 1);
        var raw = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.RetriesCount)?.PropertyValue;
        var retries = int.TryParse(raw, out var n) ? n : 1;
        retries = Math.Clamp(retries, 1, 3);
        return new StepRetryPolicy(true, retries + 1);
    }

    public static bool IsRetryable(Exception ex, CancellationToken ct)
    {
        // Any cancellation is non-retryable, whether user-cancel or peer fail-fast.
        if (ex is OperationCanceledException) return false;
        if (ex is DeploymentAbortedException) return false;
        // Do not retry transient infra failures that must propagate for resume
        if (TransientFailureClassifier.IsTransient(ex)) return false;
        return true;
    }
}
