namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// A single deployment step in the resolved <see cref="DeploymentPlan"/>. Mirrors the
/// structure of <c>DeploymentStepDto</c> but is the planner's own output — with
/// applicability, role matching, and per-target dispatch already resolved.
/// </summary>
public sealed record PlannedStep
{
    /// <summary>Primary key of the underlying step.</summary>
    public required int StepId { get; init; }

    /// <summary>Display name of the step.</summary>
    public required string StepName { get; init; }

    /// <summary>Original ordering of the step inside the deployment process.</summary>
    public required int StepOrder { get; init; }

    /// <summary>The planner's verdict on this step. See <see cref="PlannedStepStatus"/>.</summary>
    public required PlannedStepStatus Status { get; init; }

    /// <summary>
    /// Human-readable explanation for <see cref="Status"/>. Empty when the step is fully applicable.
    /// </summary>
    public string StatusMessage { get; init; } = string.Empty;

    /// <summary>The roles required by this step as declared on the step properties.</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = Array.Empty<string>();

    /// <summary>Targets that matched this step's required roles (empty for step-level / server-only / skipped steps).</summary>
    public IReadOnlyList<PlannedTarget> MatchedTargets { get; init; } = Array.Empty<PlannedTarget>();

    /// <summary>Runnable actions on this step (env/channel/skip-list filtered).</summary>
    public IReadOnlyList<PlannedAction> Actions { get; init; } = Array.Empty<PlannedAction>();

    /// <summary>
    /// All dispatches across all actions on this step. Flattened for callers that don't
    /// need per-action nesting (e.g. per-target log framing in the executor).
    /// </summary>
    public IReadOnlyList<PlannedTargetDispatch> Dispatches =>
        Actions.SelectMany(a => a.Dispatches).ToList();

    /// <summary><c>true</c> when <see cref="Status"/> is <see cref="PlannedStepStatus.Applicable"/>.</summary>
    public bool IsApplicable => Status == PlannedStepStatus.Applicable;
}
