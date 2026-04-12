using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Rendering;

/// <summary>
/// Everything a renderer needs beyond the intent itself to produce a transport-specific
/// <see cref="Script.ScriptExecutionRequest"/>. Assembled by <c>ExecuteStepsPhase</c> at
/// dispatch time and passed unchanged to the resolved <see cref="IIntentRenderer"/>.
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
    /// Target namespace for the deployment action, resolved from endpoint variables.
    /// Transports that support namespace isolation (e.g. Kubernetes) use this to
    /// scope script execution. <c>null</c> when the transport has no namespace concept.
    /// </summary>
    public string? TargetNamespace { get; init; }

    /// <summary>
    /// Post-acquisition package references for this action, matched from acquired packages
    /// by action name. Populated by the pipeline at dispatch time.
    /// </summary>
    public IReadOnlyList<PackageAcquisitionResult> PackageReferences { get; init; } = Array.Empty<PackageAcquisitionResult>();
}
