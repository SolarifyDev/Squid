using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawWaitSessionActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawWaitSession;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var sessionKey = ctx.Action.GetProperty("Squid.Action.OpenClaw.SessionKey");
        var successPattern = ctx.Action.GetProperty("Squid.Action.OpenClaw.SuccessPattern");
        var failPattern = ctx.Action.GetProperty("Squid.Action.OpenClaw.FailPattern");
        var maxWaitSeconds = ctx.Action.GetProperty("Squid.Action.OpenClaw.MaxWaitSeconds");
        var pollSeconds = ctx.Action.GetProperty("Squid.Action.OpenClaw.PollSeconds");

        var result = new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ScriptBody = $"# OpenClaw WaitSession: {sessionKey}",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>
            {
                ["OpenClaw.ActionKind"] = "WaitSession",
                ["OpenClaw.SessionKey"] = sessionKey ?? string.Empty,
                ["OpenClaw.SuccessPattern"] = successPattern ?? string.Empty,
                ["OpenClaw.FailPattern"] = failPattern ?? string.Empty,
                ["OpenClaw.MaxWaitSeconds"] = maxWaitSeconds ?? "120",
                ["OpenClaw.PollSeconds"] = pollSeconds ?? "5"
            }
        };

        return Task.FromResult(result);
    }
}
