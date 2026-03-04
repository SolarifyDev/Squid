using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployIngressActionHandler : IActionHandler
{
    private readonly KubernetesIngressActionYamlGenerator _generator = new();

    public DeploymentActionType ActionType => DeploymentActionType.KubernetesDeployIngress;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return DeploymentActionTypeParser.Is(action.ActionType, ActionType);
    }

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var files = await _generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false);

        if (files == null || files.Count == 0)
            return null;

        var result = new ActionExecutionResult
        {
            ScriptBody = "kubectl apply -f \"./ingress.yaml\"",
            Files = files,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        };

        return result;
    }
}
