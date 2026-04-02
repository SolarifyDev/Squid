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
        return Task.FromResult(new ActionExecutionResult
        {
            ScriptBody = $"# OpenClaw InvokeTool: {ctx.Action.GetProperty(SpecialVariables.OpenClaw.PropTool)}",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        });
    }
}
