using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Script;

internal static class ScriptExecutionPlanFactory
{
    public static ScriptExecutionPlan Create(ScriptExecutionRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        return request.ResolveExecutionMode() switch
        {
            ExecutionMode.DirectScript => new DirectScriptExecutionPlan(request),
            ExecutionMode.PackagedPayload => new PackagedPayloadExecutionPlan(request),
            _ => throw new InvalidOperationException($"Unsupported execution mode '{request.ResolveExecutionMode()}'.")
        };
    }
}
