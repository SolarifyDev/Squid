using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering.Adapters;
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
    /// Phase 9a seam: every handler eventually produces an <see cref="ExecutionIntent"/>
    /// directly. Until each handler is migrated (Phase 9b+), the default implementation
    /// routes through the legacy <see cref="PrepareAsync"/> path and adapts the result
    /// via <see cref="LegacyIntentAdapter"/>. Handlers opt in to the new model by
    /// providing an explicit interface implementation that bypasses <c>PrepareAsync</c>
    /// entirely.
    /// </summary>
    async Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var result = await PrepareAsync(ctx, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(result.ActionName))
            result.ActionName = ctx.Action?.Name;

        if (string.IsNullOrEmpty(result.ActionType))
            result.ActionType = ctx.Action?.ActionType;

        var stepName = ctx.Step?.Name ?? string.Empty;

        return LegacyIntentAdapter.FromLegacyResult(result, stepName);
    }

    Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct) =>
        throw new NotSupportedException($"Handler {GetType().Name} does not support step-level execution");
}
