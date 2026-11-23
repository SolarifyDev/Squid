using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution;

public class DeploymentTaskContext
{
    // Task Identity (available before LoadTaskAsync — set at pipeline entry)
    public int ServerTaskId { get; init; }

    // Task & Deployment
    public ServerTask Task { get; set; }
    public Deployment Deployment { get; set; }
    public Release Release { get; set; }
    public Project Project { get; set; }
    public Squid.Core.Persistence.Entities.Deployments.Environment Environment { get; set; }
    public Channel Channel { get; set; }
    public DeploymentProcessSnapshotDto ProcessSnapshot { get; set; }

    // Variables — deployment-level only, not polluted by endpoint variables
    public List<VariableDto> Variables { get; set; }

    // Targets
    public List<Machine> AllTargets { get; set; } = new();
    public List<Machine> ExcludedByHealthTargets { get; set; }
    public List<DeploymentTargetContext> AllTargetsContext { get; set; } = new();

    // Execution
    public List<DeploymentStepDto> Steps { get; set; }
    public List<ReleaseSelectedPackage> SelectedPackages { get; set; } = new();
    public Dictionary<string, PackageAcquisitionResult> AcquiredPackages { get; set; } = new();
    public bool FailureEncountered { get; set; }
    public bool UseGuidedFailure { get; set; }

    /// <summary>
    /// Set by the pipeline runner, which alone knows why a run is being interrupted. Copied onto each
    /// dispatched <c>ScriptExecutionRequest</c> so a remote script is cancelled only when the task is
    /// heading to a terminal state. Null (e.g. a context built outside the runner) = legacy
    /// cancel-on-every-interruption.
    /// </summary>
    public RemoteScriptCancelDecision ShouldCancelRemoteScript { get; set; }

    // Planning — deployment plan produced by IDeploymentPlanner (phase 6, order 460).
    // Shadow-observed in 6c-i: built but not consumed. Phase 6c-iii wires ExecuteStepsPhase
    // to consume Plan.Steps for per-target dispatches and switches the planner to Execute mode.
    public DeploymentPlan Plan { get; set; }

    // Server-only execution
    public bool IsServerOnlyDeployment { get; set; }

    // Resume
    public bool IsResume { get; set; }
    public int? ResumeFromBatchIndex { get; set; }
    public List<VariableDto> RestoredOutputVariables { get; set; } = new();

    /// <summary>
    /// Every output variable captured so far in this deployment — the exact set written to
    /// the checkpoint by <see cref="Variables.CheckpointOutputVariableSerializer"/>.
    ///
    /// <para>Accumulated at the capture site instead of being re-derived by name at persist
    /// time: output variables are minted in three forms (step-qualified, machine-qualified
    /// and a bare alias), and the bare alias is indistinguishable from an ordinary variable
    /// by name, so no name predicate can select them correctly. On resume this is re-seeded
    /// from the restored set so a SECOND pause still checkpoints the first run's outputs.</para>
    ///
    /// <para>De-duplication and at-rest protection are owned by
    /// <see cref="Variables.CapturedOutputVariableSet"/> — see its remarks for why both
    /// belong at the capture site rather than at serialize time.</para>
    /// </summary>
    public Variables.CapturedOutputVariableSet CapturedOutputVariables { get; } = new();

    /// <summary>
    /// Per-batch per-target completion state restored from checkpoint. The executor
    /// skips any (batch, machineId) pair that is already marked terminal here.
    /// </summary>
    public Dictionary<int, Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState> ResumeBatchStates { get; set; } = new();

    // Lease held while the pipeline runs; IsLost means the task was recovered from under this run
    public Squid.Core.Services.Deployments.ServerTask.Lease.IServerTaskLease Lease { get; set; }

    /// <summary>
    /// Identity of THIS run on the task's lease (process identity plus a per-run suffix). The claim stamps it, the keeper renews it, and the run's pause,
    /// success, failure and cancel writes apply only while the row still carries it — so a second run of the task, started while this one is still
    /// unwinding, is never overwritten by it.
    /// </summary>
    public string LeaseOwner { get; init; } = Squid.Core.Services.Deployments.ServerTask.Lease.ServerTaskLeaseOwner.NewRun();

    private readonly CancellationTokenSource _suspend = new();
    private volatile DeploymentSuspendedException _suspended;

    /// <summary>Cancelled when a step suspends the task (manual intervention, guided failure) so every sibling stops instead of running on behind a Paused task.</summary>
    public CancellationToken SuspendToken => _suspend.Token;

    /// <summary>The suspension that stopped this run, or null while the run is not suspended. Also read by the cancel decision: a suspended run is pausing, not ending, so its remote scripts are left running.</summary>
    public DeploymentSuspendedException Suspended => _suspended;

    /// <summary>
    /// A step has already moved the task to Paused (its lease is released): stop everything else this run is doing. Call it only AFTER that write
    /// succeeded, because the siblings it cancels leave their agent scripts running on the strength of the task being Paused.
    /// </summary>
    public void RequestSuspend(DeploymentSuspendedException suspended)
    {
        _suspended ??= suspended;

        _suspend.Cancel();
    }

    // Logging
    private long _logSequence;
    public long NextLogSequence() => Interlocked.Increment(ref _logSequence);
}

public class DeploymentTargetContext
{
    public Machine Machine { get; set; }
    public EndpointContext EndpointContext { get; set; } = new();
    public CommunicationStyle CommunicationStyle { get; set; }
    public IDeploymentTransport Transport { get; set; }

    // Isolated endpoint variables (not polluting global _ctx.Variables)
    public List<VariableDto> EndpointVariables { get; set; } = new();

    // Exclusion state — set by health checks, guided failure, or any future filter
    public bool IsExcluded { get; private set; }
    public string ExclusionReason { get; private set; }

    public void Exclude(string reason)
    {
        IsExcluded = true;
        ExclusionReason = reason;
    }
}
