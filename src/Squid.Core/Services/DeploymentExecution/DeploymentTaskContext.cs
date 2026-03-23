using Squid.Core.Persistence.Entities.Deployments;
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
    public bool FailureEncountered { get; set; }
    public bool UseGuidedFailure { get; set; }

    // Resume
    public bool IsResume { get; set; }
    public int? ResumeFromBatchIndex { get; set; }
    public List<VariableDto> RestoredOutputVariables { get; set; } = new();

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
