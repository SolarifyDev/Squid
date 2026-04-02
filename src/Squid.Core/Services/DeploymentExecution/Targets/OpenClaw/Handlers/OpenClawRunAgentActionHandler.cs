using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawRunAgentActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawRunAgent;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var message = ctx.Action.GetProperty("Squid.Action.OpenClaw.Message") ?? string.Empty;
        var agentId = ctx.Action.GetProperty("Squid.Action.OpenClaw.AgentId");
        var sessionKey = ctx.Action.GetProperty("Squid.Action.OpenClaw.SessionKey");
        var wakeMode = ctx.Action.GetProperty("Squid.Action.OpenClaw.WakeMode");
        var deliver = ctx.Action.GetProperty("Squid.Action.OpenClaw.Deliver");
        var channel = ctx.Action.GetProperty("Squid.Action.OpenClaw.Channel");
        var to = ctx.Action.GetProperty("Squid.Action.OpenClaw.To");
        var timeoutSeconds = ctx.Action.GetProperty("Squid.Action.OpenClaw.TimeoutSeconds");

        var result = new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ScriptBody = $"# OpenClaw RunAgent: {message}",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>
            {
                ["OpenClaw.ActionKind"] = "RunAgent",
                ["OpenClaw.Message"] = message,
                ["OpenClaw.AgentId"] = agentId ?? string.Empty,
                ["OpenClaw.SessionKey"] = sessionKey ?? string.Empty,
                ["OpenClaw.WakeMode"] = wakeMode ?? string.Empty,
                ["OpenClaw.Deliver"] = deliver ?? string.Empty,
                ["OpenClaw.Channel"] = channel ?? string.Empty,
                ["OpenClaw.To"] = to ?? string.Empty,
                ["OpenClaw.TimeoutSeconds"] = timeoutSeconds ?? string.Empty
            }
        };

        return Task.FromResult(result);
    }
}
