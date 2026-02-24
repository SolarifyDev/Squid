using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.ExecutionPlans;

internal abstract class ScriptExecutionPlan
{
    protected ScriptExecutionPlan(ScriptExecutionRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public ScriptExecutionRequest Request { get; }

    public abstract ExecutionMode Mode { get; }
}

internal sealed class DirectScriptExecutionPlan : ScriptExecutionPlan
{
    public DirectScriptExecutionPlan(ScriptExecutionRequest request)
        : base(request)
    {
    }

    public override ExecutionMode Mode => ExecutionMode.DirectScript;

    public string ScriptBody => Request.ScriptBody;

    public Dictionary<string, byte[]> Files => Request.Files;
}

internal sealed class PackagedPayloadExecutionPlan : ScriptExecutionPlan
{
    public PackagedPayloadExecutionPlan(ScriptExecutionRequest request)
        : base(request)
    {
    }

    public override ExecutionMode Mode => ExecutionMode.PackagedPayload;

    public Dictionary<string, byte[]> Files => Request.Files;
}
