using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Script;

public class ScriptExecutionRequest
{
    public string ScriptBody { get; set; }

    /// <summary>
    /// Typed, validated collection of files that must accompany the script on the target.
    /// </summary>
    public DeploymentFileCollection DeploymentFiles { get; set; } = DeploymentFileCollection.Empty;

    public string CalamariCommand { get; set; }
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Unspecified;
    public ContextPreparationPolicy ContextPreparationPolicy { get; set; } = ContextPreparationPolicy.Unspecified;
    public ExecutionLocation ExecutionLocation { get; set; } = ExecutionLocation.Unspecified;
    public ExecutionBackend ExecutionBackend { get; set; } = ExecutionBackend.Unspecified;
    public PayloadKind PayloadKind { get; set; } = PayloadKind.Unspecified;
    public RunnerKind RunnerKind { get; set; } = RunnerKind.Unspecified;
    public ScriptSyntax Syntax { get; set; } = ScriptSyntax.PowerShell;
    public string ActionType { get; set; }
    public Dictionary<string, string> ActionProperties { get; set; }
    public EndpointContext EndpointContext { get; set; }
    public List<VariableDto> Variables { get; set; }
    public Persistence.Entities.Deployments.Machine Machine { get; set; }
    public string ReleaseVersion { get; set; }
    public TimeSpan? Timeout { get; set; }
    public SensitiveValueMasker Masker { get; set; }
    public string? TargetNamespace { get; set; }
    public int ServerTaskId { get; set; }
    public List<PackageAcquisitionResult> PackageReferences { get; set; } = new();
    public string StepName { get; set; }
    public string ActionName { get; set; }

    // Stable, process-unique ids of the step/action that produced this dispatch.
    // Used to key the in-flight reattach slot (StepName/ActionName are display
    // names with no uniqueness guarantee, so they cannot key concurrent
    // same-machine dispatches). Set post-render in ExecuteStepsPhase.
    public int StepId { get; set; }
    public int ActionId { get; set; }

    /// <summary>
    /// Optional live-output sink. When set, an execution strategy that observes incremental output
    /// (the Halibut/Tentacle poll loop) invokes it with each new batch of lines as they arrive, so
    /// the task log tails live. Runtime-only (never serialized to an agent); null = legacy behavior.
    /// </summary>
    public ScriptOutputSink OutputSink { get; set; }

    public ExecutionMode ResolveExecutionMode()
    {
        if (ExecutionMode == ExecutionMode.Unspecified)
            throw new InvalidOperationException("ScriptExecutionRequest.ExecutionMode must be explicitly set.");

        return ExecutionMode;
    }

    public ContextPreparationPolicy ResolveContextPreparationPolicy()
    {
        if (ContextPreparationPolicy != ContextPreparationPolicy.Unspecified)
            return ContextPreparationPolicy;

        return ResolveExecutionMode() == ExecutionMode.PackagedPayload
            ? ContextPreparationPolicy.Skip
            : ContextPreparationPolicy.Apply;
    }
}
