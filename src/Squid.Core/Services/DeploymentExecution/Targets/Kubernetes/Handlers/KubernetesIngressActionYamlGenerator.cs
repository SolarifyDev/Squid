using System.Text;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesIngressActionYamlGenerator : IActionYamlGenerator
{
    private static readonly string IngressActionType = SpecialVariables.ActionTypes.KubernetesDeployIngress;

    private readonly IngressResourceGenerator _generator = new();

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null)
            return false;

        return string.Equals(action.ActionType, IngressActionType, StringComparison.OrdinalIgnoreCase);
    }

    public Task<Dictionary<string, byte[]>> GenerateAsync(DeploymentStepDto step, DeploymentActionDto action, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>();

        if (!CanHandle(action))
            return Task.FromResult(result);

        var properties = KubernetesPropertyParser.BuildPropertyDictionary(action);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_generator.CanGenerate(properties))
            return Task.FromResult(result);

        var yaml = _generator.Generate(properties);

        if (!string.IsNullOrWhiteSpace(yaml))
            result["ingress.yaml"] = Encoding.UTF8.GetBytes(yaml);

        return Task.FromResult(result);
    }
}
