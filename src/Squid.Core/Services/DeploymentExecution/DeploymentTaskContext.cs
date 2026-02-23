using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public class DeploymentTaskContext
{
    // Task & Deployment
    public Persistence.Entities.Deployments.ServerTask Task { get; set; }
    public Deployment Deployment { get; set; }
    public Persistence.Entities.Deployments.Release Release { get; set; }
    public DeploymentProcessSnapshotDto ProcessSnapshot { get; set; }

    // Variables — deployment-level only, not polluted by endpoint variables
    public List<VariableDto> Variables { get; set; }

    // Targets
    public List<Persistence.Entities.Deployments.Machine> AllTargets { get; set; } = new();
    public List<DeploymentTargetContext> AllTargetsContext { get; set; } = new();

    // Execution
    public List<DeploymentStepDto> Steps { get; set; }
    public List<Persistence.Entities.Deployments.ReleaseSelectedPackage> SelectedPackages { get; set; } = new();
    public bool FailureEncountered { get; set; }

    // Calamari
    public byte[] CalamariPackageBytes { get; set; }

    // Activity Tracking
    public Persistence.Entities.Deployments.ActivityLog TaskActivityNode { get; set; }

    // Logging
    private long _logSequence;
    public long NextLogSequence() => Interlocked.Increment(ref _logSequence);
}

public class DeploymentTargetContext
{
    public Persistence.Entities.Deployments.Machine Machine { get; set; }
    public DeploymentAccount Account { get; set; }
    public string EndpointJson { get; set; }
    public CommunicationStyle CommunicationStyle { get; set; }
    public IDeploymentTransport Transport { get; set; }
    public byte[] CalamariPackageBytes { get; set; }

    // Isolated endpoint variables (not polluting global _ctx.Variables)
    public List<VariableDto> EndpointVariables { get; set; } = new();
}
