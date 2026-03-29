using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesRunScriptActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.KubernetesRunScript;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var userScript = ctx.Action.GetProperty(SpecialVariables.Action.ScriptBody) ?? string.Empty;
        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);

        var result = new ActionExecutionResult
        {
            ScriptBody = userScript,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };

        return Task.FromResult(result);
    }
}
