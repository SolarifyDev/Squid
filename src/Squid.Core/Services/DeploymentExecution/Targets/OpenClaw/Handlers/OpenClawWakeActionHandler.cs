using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawWakeActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawWake;

    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
        => Task.FromResult<ExecutionIntent>(OpenClawIntentFactory.Build(ctx, OpenClawInvocationKind.Wake, "openclaw-wake"));
}
