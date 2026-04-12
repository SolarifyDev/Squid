using Squid.Core.Services.DeploymentExecution.Intents;
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

    /// <summary>
    /// Emits an <see cref="ExecutionIntent"/> describing what the action wants to do.
    /// The pipeline expands variables in the intent, applies structured config replacement,
    /// then passes the intent to the per-transport <see cref="Rendering.IIntentRenderer"/>
    /// which produces a <see cref="Script.ScriptExecutionRequest"/>.
    /// </summary>
    Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct);

    Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct) =>
        throw new NotSupportedException($"Handler {GetType().Name} does not support step-level execution");
}
