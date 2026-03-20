using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public enum ExecutionScope
{
    TargetLevel = 0,
    StepLevel = 1
}

public interface IActionHandler : IScopedDependency
{
    string ActionType { get; }

    ExecutionScope ExecutionScope => ExecutionScope.TargetLevel;

    bool CanHandle(DeploymentActionDto action)
        => string.Equals(action?.ActionType, ActionType, StringComparison.OrdinalIgnoreCase);

    Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct);

    Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct) =>
        throw new NotSupportedException($"Handler {GetType().Name} does not support step-level execution");
}
