using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawAssertActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.OpenClawAssert;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(new ActionExecutionResult
        {
            ScriptBody = "# OpenClaw Assert",
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        });
    }
}
