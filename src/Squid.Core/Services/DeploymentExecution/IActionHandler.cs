using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution;

public enum ExecutionScope
{
    TargetLevel = 0,
    StepLevel = 1
}

public interface IActionHandler : IScopedDependency
{
    DeploymentActionType ActionType { get; }

    ExecutionScope ExecutionScope => ExecutionScope.TargetLevel;

    bool CanHandle(DeploymentActionDto action);

    Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct);
}
