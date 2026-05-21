using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Validation;
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

    /// <summary>
    /// Static, plan-time-known capability requirements this handler places on the
    /// target machine. Map of <c>slot → acceptable values</c>; the target satisfies
    /// the slot if it advertises AT LEAST ONE value in the acceptable-values set.
    ///
    /// <para><b>AND across slots, OR within a slot.</b> A handler that needs Windows
    /// AND PowerShell declares two slots; a handler that runs on Windows OR Linux
    /// declares a single <c>os</c> slot with both values.</para>
    ///
    /// <para><b>Why this is on the handler interface (not intent)</b>: requirements
    /// known BEFORE intent generation (i.e. before <see cref="DescribeIntentAsync"/>
    /// runs) let <c>DeploymentPlanner</c> filter out targets at preview time. The
    /// existing <c>ExecutionIntent.RequiredCapabilities</c> covers run-time-derived
    /// requirements (Phase 5.5 feature gating). The two surfaces compose.</para>
    ///
    /// <para><b>Default empty</b> — handlers that don't declare requirements run on
    /// any target the role match selects, exactly like before. See
    /// <see cref="CapabilityRequirements"/> for the fluent builder.</para>
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlySet<string>> StaticRequirements
        => CapabilityRequirements.Empty;

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
