using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawWakeActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawWake;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var text = ctx.Action.GetProperty("Squid.Action.OpenClaw.WakeText") ?? string.Empty;
        var mode = ctx.Action.GetProperty("Squid.Action.OpenClaw.WakeMode");
        var timeoutSeconds = ctx.Action.GetProperty("Squid.Action.OpenClaw.TimeoutSeconds");

        var result = new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ScriptBody = $"# OpenClaw Wake: {text}",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>
            {
                ["OpenClaw.ActionKind"] = "Wake",
                ["OpenClaw.WakeText"] = text,
                ["OpenClaw.WakeMode"] = mode ?? "now",
                ["OpenClaw.TimeoutSeconds"] = timeoutSeconds ?? string.Empty
            }
        };

        return Task.FromResult(result);
    }
}
