using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Rendering;

/// <summary>
/// Everything a renderer needs beyond the intent itself to produce a transport-specific
/// <see cref="ScriptExecutionRequest"/>. Assembled by <c>ExecuteStepsPhase</c> at
/// dispatch time and passed unchanged to the resolved <see cref="IIntentRenderer"/>.
///
/// <para>
/// <b>Phase 5 bridge:</b> the <see cref="LegacyRequest"/> property carries the pre-built
/// <see cref="ScriptExecutionRequest"/> produced by the legacy <c>BuildScriptExecutionRequest</c>
/// path. In Phase 5 every concrete renderer simply returns it unchanged — preserving
/// behaviour while the renderer abstraction is wired through the pipeline and covered
/// with tests. Phase 9 removes this field once handlers emit real semantic intents.
/// </para>
/// </summary>
public sealed class IntentRenderContext
{
    /// <summary>The target being dispatched to (machine, account, endpoint JSON, transport).</summary>
    public required DeploymentTargetContext Target { get; init; }

    /// <summary>The deployment step this intent was derived from.</summary>
    public required DeploymentStepDto Step { get; init; }

    /// <summary>Effective variables for this target/action (deployment vars + endpoint vars + action scope).</summary>
    public required IReadOnlyList<VariableDto> EffectiveVariables { get; init; }

    /// <summary>Release version string, or <c>null</c> for a non-release-driven deployment.</summary>
    public string? ReleaseVersion { get; init; }

    /// <summary>Server task id of the currently-executing deployment.</summary>
    public int ServerTaskId { get; init; }

    /// <summary>Optional per-step wall-clock timeout; <c>null</c> means "transport default".</summary>
    public TimeSpan? StepTimeout { get; init; }

    /// <summary>
    /// Phase 5 bridge: the <see cref="ScriptExecutionRequest"/> built by the legacy
    /// <c>BuildScriptExecutionRequest</c> path. Phase-5 renderers pass it through unchanged.
    /// </summary>
    public ScriptExecutionRequest? LegacyRequest { get; init; }
}
