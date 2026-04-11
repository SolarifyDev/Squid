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

    Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct);

    /// <summary>
    /// Phase 9 intent seam: every handler emits an <see cref="ExecutionIntent"/>
    /// directly. Post Phase 9k.1 there is no legacy adapter fallback — every concrete
    /// handler must provide an explicit implementation of this method (bypassing
    /// <see cref="PrepareAsync"/> entirely). The pipeline reaches handlers via this
    /// method only; <see cref="PrepareAsync"/> remains on the interface so legacy
    /// preparation state (<see cref="ActionExecutionResult.Files"/>, warnings, masker,
    /// etc.) can still flow through <see cref="IntentRenderContext.LegacyRequest"/>
    /// until Phase 10.
    /// </summary>
    Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct);

    Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct) =>
        throw new NotSupportedException($"Handler {GetType().Name} does not support step-level execution");
}
