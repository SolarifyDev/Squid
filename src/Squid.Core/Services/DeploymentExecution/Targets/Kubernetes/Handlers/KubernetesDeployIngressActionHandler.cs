using Squid.Core.Extensions;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployIngressActionHandler : IActionHandler
{
    private readonly KubernetesIngressActionYamlGenerator _generator = new();

    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeployIngress;

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var files = await _generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false);

        if (files == null || files.Count == 0)
            return null;

        var result = new ActionExecutionResult
        {
            ScriptBody = KubernetesApplyCommandBuilder.Build("./ingress.yaml", ctx.Action, ScriptSyntax.Bash),
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
