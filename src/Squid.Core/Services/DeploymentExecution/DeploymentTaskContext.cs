using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Planning;
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
    /// Per-batch per-target completion state restored from checkpoint. The executor
    /// skips any (batch, machineId) pair that is already marked terminal here.
    /// </summary>
    public Dictionary<int, Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState> ResumeBatchStates { get; set; } = new();

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
