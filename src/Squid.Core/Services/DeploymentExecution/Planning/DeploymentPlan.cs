namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// The complete deployment plan produced by <see cref="IDeploymentPlanner"/>. Consumable
/// by both the Preview UI (<see cref="PlanMode.Preview"/>) and the executor
/// (<see cref="PlanMode.Execute"/>) — the only difference between the two modes is whether
/// blockers throw or are surfaced.
///
/// <para>
/// A plan is an immutable snapshot of "what would happen if we ran this deployment now"
/// at the moment the planner was invoked. Nothing inside it mutates over time.
/// </para>
/// </summary>
public sealed record DeploymentPlan
{
    /// <summary>Which mode produced this plan.</summary>
    public required PlanMode Mode { get; init; }

    /// <summary>The release id the plan belongs to.</summary>
    public required int ReleaseId { get; init; }

    /// <summary>The environment id the plan targets.</summary>
    public required int EnvironmentId { get; init; }

    /// <summary>The deployment process snapshot id that the plan was derived from.</summary>
    public required int DeploymentProcessSnapshotId { get; init; }

    /// <summary>Every step the planner evaluated, in original order.</summary>
    public IReadOnlyList<PlannedStep> Steps { get; init; } = Array.Empty<PlannedStep>();

    /// <summary>
    /// The candidate machines considered during planning — after environment, disabled, health,
    /// and machine-selection filters were applied.
    /// </summary>
    public IReadOnlyList<PlannedTarget> CandidateTargets { get; init; } = Array.Empty<PlannedTarget>();

    /// <summary>
    /// Every reason the plan cannot be executed. An empty list means the plan is fully runnable.
    /// </summary>
    public IReadOnlyList<PlanBlockingReason> BlockingReasons { get; init; } = Array.Empty<PlanBlockingReason>();

    /// <summary><c>true</c> when <see cref="BlockingReasons"/> is empty.</summary>
    public bool CanProceed => BlockingReasons.Count == 0;
}
