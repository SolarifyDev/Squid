using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawFetchResultActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawFetchResult;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var sessionKey = ctx.Action.GetProperty("Squid.Action.OpenClaw.SessionKey");
        var fieldMappings = ctx.Action.GetProperty("Squid.Action.OpenClaw.FieldMappings");
        var sourceVariable = ctx.Action.GetProperty("Squid.Action.OpenClaw.SourceVariable");

        var result = new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ScriptBody = "# OpenClaw FetchResult",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>
            {
                ["OpenClaw.ActionKind"] = "FetchResult",
                ["OpenClaw.SessionKey"] = sessionKey ?? string.Empty,
                ["OpenClaw.FieldMappings"] = fieldMappings ?? string.Empty,
                ["OpenClaw.SourceVariable"] = sourceVariable ?? SpecialVariables.OpenClaw.ResultJson
            }
        };

        return Task.FromResult(result);
    }
}
