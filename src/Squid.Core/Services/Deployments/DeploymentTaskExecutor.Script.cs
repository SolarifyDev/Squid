using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private static void CaptureOutputVariables(ActionExecutionResult actionResult, List<string> logLines)
    {
        var outputVars = ServiceMessageParser.ParseOutputVariables(logLines);

        foreach (var kv in outputVars)
            actionResult.OutputVariables[kv.Key] = kv.Value.Value;
    }
}
