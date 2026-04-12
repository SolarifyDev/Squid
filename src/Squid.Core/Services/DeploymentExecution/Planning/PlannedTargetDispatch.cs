using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// A single (action × target) pair in the deployment plan. Carries the semantic
/// <see cref="Intent"/> produced for the dispatch, the <see cref="Validation"/> outcome
/// of checking that intent against the target's transport capabilities, and — when the
/// plan was built in <see cref="PlanMode.Execute"/> or with
/// <see cref="DeploymentPlanRequest.IncludeRenderedRequests"/> — a pre-rendered
/// <see cref="ScriptExecutionRequest"/> the executor can dispatch verbatim.
/// </summary>
public sealed record PlannedTargetDispatch
{
    /// <summary>The target this dispatch goes to.</summary>
    public required PlannedTarget Target { get; init; }

    /// <summary>The semantic intent produced for this dispatch.</summary>
    public required ExecutionIntent Intent { get; init; }

    /// <summary>Capability validation result. When <c>IsValid = false</c> the dispatch is blocked.</summary>
    public CapabilityValidationResult Validation { get; init; } = CapabilityValidationResult.Supported;

    /// <summary>
    /// Optional concrete <see cref="ScriptExecutionRequest"/> rendered from <see cref="Intent"/>
    /// via the transport's <c>IIntentRenderer</c>. Populated in Execute mode, and in Preview
    /// mode when the caller asks for rendered requests.
    /// </summary>
    public ScriptExecutionRequest? RenderedRequest { get; init; }
}
