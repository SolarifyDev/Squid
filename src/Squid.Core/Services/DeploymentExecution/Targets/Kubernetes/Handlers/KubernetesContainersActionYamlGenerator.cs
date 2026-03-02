using System.Text;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesContainersActionYamlGenerator : IActionYamlGenerator
{
    private const string ContainersActionType = "Squid.KubernetesDeployContainers";

    private readonly DeploymentResourceGenerator _deployment = new();
    private readonly ServiceResourceGenerator _service = new();
    private readonly ConfigMapResourceGenerator _configMap = new();
    private readonly IngressResourceGenerator _ingress = new();
    private readonly SecretResourceGenerator _secret = new();

    public bool CanHandle(DeploymentActionDto action)
    {
        if (action == null)
            return false;

        return string.Equals(action.ActionType, ContainersActionType, StringComparison.OrdinalIgnoreCase);
    }

    public Task<Dictionary<string, byte[]>> GenerateAsync(DeploymentStepDto step, DeploymentActionDto action, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>();

        if (!CanHandle(action))
            return Task.FromResult(result);

        var properties = KubernetesPropertyParser.BuildPropertyDictionary(action);

        NormalizeDeploymentName(action, properties);

        cancellationToken.ThrowIfCancellationRequested();

        AddResource(result, "deployment.yaml", _deployment, properties);
        AddResource(result, "service.yaml", _service, properties);
        AddResource(result, "configmap.yaml", _configMap, properties);
        AddResource(result, "ingress.yaml", _ingress, properties);
        AddResource(result, "secret.yaml", _secret, properties);

        return Task.FromResult(result);
    }

    private static void NormalizeDeploymentName(DeploymentActionDto action, Dictionary<string, string> properties)
    {
        const string key = KubernetesProperties.DeploymentName;

        if (!properties.TryGetValue(key, out var name) || string.IsNullOrWhiteSpace(name))
            properties[key] = action.Name;
    }

    private static void AddResource(Dictionary<string, byte[]> result, string fileName, IKubernetesResourceGenerator generator, Dictionary<string, string> properties)
    {
        if (!generator.CanGenerate(properties))
            return;

        var yaml = generator.Generate(properties);

        if (!string.IsNullOrWhiteSpace(yaml))
            result[fileName] = Encoding.UTF8.GetBytes(yaml);
    }
}
