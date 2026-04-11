using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// The inputs <see cref="IDeploymentPlanner"/> needs to produce a <see cref="DeploymentPlan"/>.
/// Intentionally a plain record — the planner is a pure function over these inputs and does
/// not load entities itself, making it trivial to test.
/// </summary>
public sealed record DeploymentPlanRequest
{
    /// <summary>Preview or Execute. See <see cref="PlanMode"/>.</summary>
    public required PlanMode Mode { get; init; }

    /// <summary>The release id associated with the deployment.</summary>
    public required int ReleaseId { get; init; }

    /// <summary>The deployment environment id.</summary>
    public required int EnvironmentId { get; init; }

    /// <summary>The release channel id (controls per-action channel filtering).</summary>
    public required int ChannelId { get; init; }

    /// <summary>The deployment process snapshot id that <see cref="Steps"/> was derived from.</summary>
    public required int DeploymentProcessSnapshotId { get; init; }

    /// <summary>
    /// Steps the planner walks. In the pipeline these are produced by
    /// <c>ProcessSnapshotStepConverter.Convert(...)</c> on the deployment process snapshot.
    /// </summary>
    public required IReadOnlyList<DeploymentStepDto> Steps { get; init; }

    /// <summary>Effective deployment variables used for step-condition evaluation.</summary>
    public required IReadOnlyList<VariableDto> Variables { get; init; }

    /// <summary>
    /// The per-target context pool the planner iterates for target-level dispatches. Callers
    /// are responsible for resolving the transport on each context (the planner reads
    /// <c>DeploymentTargetContext.Transport</c> to derive capabilities and communication style).
    /// </summary>
    public required IReadOnlyList<DeploymentTargetContext> TargetContexts { get; init; }

    /// <summary>Actions the user manually asked to skip; matched by <c>DeploymentActionDto.Id</c>.</summary>
    public IReadOnlySet<int> SkipActionIds { get; init; } = new HashSet<int>();

    /// <summary>When <c>true</c> the Preview planner also renders <see cref="PlannedTargetDispatch.RenderedRequest"/>.</summary>
    public bool IncludeRenderedRequests { get; init; }

    /// <summary>Optional deployment id carried through for logging context.</summary>
    public int DeploymentId { get; init; }

    /// <summary>Optional server task id carried through for logging context.</summary>
    public int ServerTaskId { get; init; }
}
