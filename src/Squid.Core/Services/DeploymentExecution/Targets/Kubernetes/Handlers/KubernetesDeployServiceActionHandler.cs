using System.Text;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesDeployServiceActionHandler : IActionHandler
{
    private readonly ServiceResourceGenerator _generator = new();

    public DeploymentActionType ActionType => DeploymentActionType.KubernetesDeployService;

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return DeploymentActionTypeParser.Is(action.ActionType, ActionType);
    }

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var properties = KubernetesPropertyParser.BuildPropertyDictionary(ctx.Action);

        if (!_generator.CanGenerate(properties))
            return Task.FromResult<ActionExecutionResult>(null);

        var yaml = _generator.Generate(properties);

        return Task.FromResult(new ActionExecutionResult
        {
            ScriptBody = "kubectl apply -f \"./service.yaml\"",
            Files = new Dictionary<string, byte[]> { ["service.yaml"] = Encoding.UTF8.GetBytes(yaml) },
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = ScriptSyntax.Bash
        });
    }
}
