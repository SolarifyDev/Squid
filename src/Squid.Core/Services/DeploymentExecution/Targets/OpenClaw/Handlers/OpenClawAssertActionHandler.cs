using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawAssertActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawAssert;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var jsonPath = ctx.Action.GetProperty("Squid.Action.OpenClaw.JsonPath") ?? string.Empty;
        var op = ctx.Action.GetProperty("Squid.Action.OpenClaw.Operator");
        var expected = ctx.Action.GetProperty("Squid.Action.OpenClaw.Expected");
        var sourceVariable = ctx.Action.GetProperty("Squid.Action.OpenClaw.SourceVariable");

        var result = new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ScriptBody = $"# OpenClaw Assert: {jsonPath} {op ?? "equals"} {expected}",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>
            {
                ["OpenClaw.ActionKind"] = "Assert",
                ["OpenClaw.JsonPath"] = jsonPath,
                ["OpenClaw.Operator"] = op ?? "equals",
                ["OpenClaw.Expected"] = expected ?? string.Empty,
                ["OpenClaw.SourceVariable"] = sourceVariable ?? SpecialVariables.OpenClaw.ResultJson
            }
        };

        return Task.FromResult(result);
    }
}
