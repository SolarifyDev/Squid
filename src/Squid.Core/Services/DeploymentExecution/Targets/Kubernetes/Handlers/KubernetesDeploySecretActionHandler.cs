using System.Text;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeploySecretActionHandler : IActionHandler
{
    private readonly SecretResourceGenerator _generator = new();

    public string ActionType => SpecialVariables.ActionTypes.KubernetesDeploySecret;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var properties = KubernetesPropertyParser.BuildPropertyDictionary(ctx.Action);

        if (!_generator.CanGenerate(properties))
            return Task.FromResult<ActionExecutionResult>(null);

        var yaml = _generator.Generate(properties);

        return Task.FromResult(new ActionExecutionResult
        {
            ScriptBody = "kubectl apply -f \"./secret.yaml\"",
            Files = new Dictionary<string, byte[]> { ["secret.yaml"] = Encoding.UTF8.GetBytes(yaml) },
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        });
    }
}
