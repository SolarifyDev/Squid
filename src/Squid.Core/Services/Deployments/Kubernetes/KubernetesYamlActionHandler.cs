using Squid.Core.Services.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesYamlActionHandler : IActionHandler
{
    private readonly IEnumerable<IActionYamlGenerator> _yamlGenerators;

    public KubernetesYamlActionHandler(IEnumerable<IActionYamlGenerator> yamlGenerators)
    {
        _yamlGenerators = yamlGenerators;
    }

    public string ActionType => "Squid.KubernetesDeployContainers";

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null) return false;

        return _yamlGenerators.Any(g => g.CanHandle(action));
    }

    public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var generator = _yamlGenerators.FirstOrDefault(g => g.CanHandle(ctx.Action));

        if (generator == null)
            return null;

        var yamlFiles = await generator.GenerateAsync(ctx.Step, ctx.Action, ct).ConfigureAwait(false);

        return new ActionExecutionResult
        {
            CalamariCommand = "calamari-kubernetes-deploy",
            Files = yamlFiles ?? new Dictionary<string, byte[]>(),
            Syntax = ScriptSyntax.PowerShell
        };
    }
}
