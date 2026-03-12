using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

public class DeploymentEventContext
{
    // Step correlation
    public int StepDisplayOrder { get; init; }
    public string StepName { get; init; }

    // Target
    public string MachineName { get; init; }
    public CommunicationStyle CommunicationStyle { get; init; }
    public List<Machine> Targets { get; init; }
    public HashSet<string> Roles { get; init; }

    // Action
    public string ActionName { get; init; }
    public string ActionType { get; init; }
    public int ActionSortOrder { get; init; }

    // Result
    public int ExitCode { get; init; }
    public bool Failed { get; init; }
    public string Error { get; init; }
    public string Message { get; init; }
    public Exception Exception { get; init; }

    // Eligibility
    public StepEligibilityResult? StepEligibility { get; init; }
    public ActionEligibilityResult? ActionEligibility { get; init; }

    // Script output
    public ScriptExecutionResult ScriptResult { get; init; }

    // Guided failure / Manual intervention
    public string GuidedFailureResolution { get; init; }
    public InterruptionType? InterruptionType { get; init; }
}

public abstract record DeploymentLifecycleEvent(DeploymentEventContext Context);

// === Deployment ===
public sealed record DeploymentStartingEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record DeploymentSucceededEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record DeploymentFailedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Target Preparation ===
public sealed record TargetsResolvedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record UnhealthyTargetsExcludedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record TargetPreparingEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record TargetTransportMissingEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record MachineConstraintsResolvedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Steps ===
public sealed record StepStartingEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record StepNoMatchingTargetsEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record StepSkippedOnTargetEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record StepConditionMetEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record StepExecutingOnTargetEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record StepCompletedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Actions (pre-execution, logged under step node) ===
public sealed record ActionManuallyExcludedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record ActionSkippedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record ActionNoHandlerEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record ActionRunningEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Actions (execution, has own activity node) ===
public sealed record ActionExecutingEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record ActionSucceededEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record ActionFailedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Script Output ===
public sealed record ScriptOutputReceivedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Guided Failure ===
public sealed record GuidedFailurePromptEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record GuidedFailureResolvedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Manual Intervention ===
public sealed record ManualInterventionPromptEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record ManualInterventionResolvedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);

// === Cancellation / Pause ===
public sealed record DeploymentCancelledEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
public sealed record DeploymentPausedEvent(DeploymentEventContext Context) : DeploymentLifecycleEvent(Context);
