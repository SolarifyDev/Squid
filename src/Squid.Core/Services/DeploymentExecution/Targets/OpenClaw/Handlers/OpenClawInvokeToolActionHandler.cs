using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawInvokeToolActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawInvokeTool;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var tool = ctx.Action.GetProperty("Squid.Action.OpenClaw.Tool") ?? string.Empty;
        var action = ctx.Action.GetProperty("Squid.Action.OpenClaw.ToolAction");
        var argsJson = ctx.Action.GetProperty("Squid.Action.OpenClaw.ArgsJson");
        var sessionKey = ctx.Action.GetProperty("Squid.Action.OpenClaw.SessionKey");
        var timeoutSeconds = ctx.Action.GetProperty("Squid.Action.OpenClaw.TimeoutSeconds");

        var result = new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ScriptBody = $"# OpenClaw InvokeTool: {tool}",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>
            {
                ["OpenClaw.ActionKind"] = "InvokeTool",
                ["OpenClaw.Tool"] = tool,
                ["OpenClaw.ToolAction"] = action ?? "json",
                ["OpenClaw.ArgsJson"] = argsJson ?? string.Empty,
                ["OpenClaw.SessionKey"] = sessionKey ?? string.Empty,
                ["OpenClaw.TimeoutSeconds"] = timeoutSeconds ?? string.Empty
            }
        };

        return Task.FromResult(result);
    }
}
