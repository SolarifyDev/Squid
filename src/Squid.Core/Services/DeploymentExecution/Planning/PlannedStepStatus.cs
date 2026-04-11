namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// The planner's verdict on what a step will do in the resulting plan. One step maps to
/// exactly one status — the first reason the planner can no longer execute the step
/// against targets wins and later checks are skipped. Status never changes after the
/// step has been emitted.
/// </summary>
public enum PlannedStepStatus
{
    /// <summary>The step runs against at least one dispatch (target-level or step-level).</summary>
    Applicable = 0,

    /// <summary>The step has <c>IsDisabled = true</c>.</summary>
    Disabled = 1,

    /// <summary>
    /// The step's condition excludes it from this run
    /// (Success when a prior step failed, Failure when no prior failure, Variable expression false).
    /// </summary>
    ConditionNotMet = 2,

    /// <summary>All actions on the step were filtered out by env/channel/skip lists.</summary>
    NoRunnableActions = 3,

    /// <summary>
    /// Every runnable action on this step is <c>ExecutionScope.StepLevel</c> (e.g. Manual Intervention).
    /// The step still runs, but it does not iterate targets — <see cref="PlannedStep.Dispatches"/> is empty.
    /// </summary>
    StepLevelOnly = 4,

    /// <summary>
    /// The step is marked <c>Squid.Action.RunOnServer = true</c>. The step still runs, but
    /// it is executed on the server rather than dispatched to a machine.
    /// </summary>
    RunOnServer = 5,

    /// <summary>
    /// No target in the candidate pool carries the step's required roles (or no targets remain
    /// after applying machine-selection constraints for a target-level step).
    /// </summary>
    NoMatchingTargets = 6
}
