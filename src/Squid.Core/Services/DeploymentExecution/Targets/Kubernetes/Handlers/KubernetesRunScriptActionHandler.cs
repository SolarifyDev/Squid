using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesRunScriptActionHandler : IActionHandler
{
    public DeploymentActionType ActionType => DeploymentActionType.KubernetesRunScript;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return DeploymentActionTypeParser.Is(action.ActionType, ActionType);
    }

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var userScript = ctx.Action.GetProperty("Squid.Action.Script.ScriptBody") ?? string.Empty;
        var syntaxStr = ctx.Action.GetProperty("Squid.Action.Script.Syntax");
        var syntax = string.Equals(syntaxStr, "Bash", StringComparison.OrdinalIgnoreCase)
            ? ScriptSyntax.Bash
            : ScriptSyntax.PowerShell;

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
