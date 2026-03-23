using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesContainersActionYamlGenerator : IActionYamlGenerator
{
    private const string ContainersActionType = "Squid.KubernetesDeployContainers";

    private readonly DeploymentResourceGenerator _deployment = new();
    private readonly ServiceResourceGenerator _service = new();
    private readonly ConfigMapResourceGenerator _configMap = new();
    private readonly IngressResourceGenerator _ingress = new();
    private readonly SecretResourceGenerator _secret = new();
    private readonly BlueGreenResourceGenerator _blueGreen = new();

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
        AppendDeploymentIdToResourceNames(properties);

        cancellationToken.ThrowIfCancellationRequested();

        if (_blueGreen.CanGenerate(properties))
        {
            foreach (var kvp in _blueGreen.GenerateAll(properties))
                result[kvp.Key] = Encoding.UTF8.GetBytes(kvp.Value);
        }
        else
        {
            AddResource(result, "deployment.yaml", _deployment, properties);
        }

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

    private static void AppendDeploymentIdToResourceNames(Dictionary<string, string> properties)
    {
        var suffix = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentIdSuffix);
        if (string.IsNullOrWhiteSpace(suffix)) return;

        var configMapBaseName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ConfigMapName);
        var secretBaseName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.SecretName);

        AppendSuffix(properties, KubernetesProperties.ConfigMapName, suffix);
        AppendSuffix(properties, KubernetesProperties.SecretName, suffix);

        RewriteContainerReferences(properties, configMapBaseName, secretBaseName, suffix);
        RewriteVolumeReferences(properties, configMapBaseName, secretBaseName, suffix);
    }

    private static void AppendSuffix(Dictionary<string, string> properties, string nameKey, string suffix)
    {
        var name = KubernetesPropertyParser.GetProperty(properties, nameKey);
        if (string.IsNullOrWhiteSpace(name)) return;

        properties[nameKey] = $"{name}-{suffix}";
    }

    private static void RewriteContainerReferences(Dictionary<string, string> properties, string configMapBaseName, string secretBaseName, string suffix)
    {
        if (!properties.TryGetValue(KubernetesProperties.Containers, out var json) || string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var containers = JsonNode.Parse(json)?.AsArray();
            if (containers == null) return;

            var modified = false;

            foreach (var container in containers)
            {
                if (container == null) continue;

                modified |= RewriteEnvFromNames(container, KubernetesContainerPayloadProperties.ConfigMapEnvFromSource, configMapBaseName, suffix);
                modified |= RewriteEnvFromNames(container, KubernetesContainerPayloadProperties.SecretEnvFromSource, secretBaseName, suffix);
                modified |= RewriteEnvVarSourceNames(container, KubernetesContainerPayloadProperties.ConfigMapEnvironmentVariables, configMapBaseName, suffix);
                modified |= RewriteEnvVarSourceNames(container, KubernetesContainerPayloadProperties.SecretEnvironmentVariables, secretBaseName, suffix);
            }

            if (modified)
                properties[KubernetesProperties.Containers] = containers.ToJsonString();
        }
        catch { /* malformed JSON — skip rewriting, parser will handle gracefully */ }
    }

    private static bool RewriteEnvFromNames(JsonNode container, string arrayKey, string baseName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return false;

        var array = container[arrayKey]?.AsArray();
        if (array == null) return false;

        var versionedName = $"{baseName}-{suffix}";
        var modified = false;

        foreach (var item in array)
        {
            if (item == null) continue;

            var name = item[KubernetesContainerEnvFromPayloadProperties.Name]?.GetValue<string>();
            if (!string.Equals(name, baseName, StringComparison.Ordinal)) continue;

            item[KubernetesContainerEnvFromPayloadProperties.Name] = versionedName;
            modified = true;
        }

        return modified;
    }

    private static bool RewriteEnvVarSourceNames(JsonNode container, string arrayKey, string baseName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return false;

        var array = container[arrayKey]?.AsArray();
        if (array == null) return false;

        var versionedName = $"{baseName}-{suffix}";
        var modified = false;

        foreach (var item in array)
        {
            if (item == null) continue;

            var sourceName = item[KubernetesContainerEnvVarSourcePayloadProperties.Value]?.GetValue<string>();
            if (!string.Equals(sourceName, baseName, StringComparison.Ordinal)) continue;

            item[KubernetesContainerEnvVarSourcePayloadProperties.Value] = versionedName;
            modified = true;
        }

        return modified;
    }

    private static void RewriteVolumeReferences(Dictionary<string, string> properties, string configMapBaseName, string secretBaseName, string suffix)
    {
        if (!properties.TryGetValue(KubernetesProperties.CombinedVolumes, out var json) || string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var volumes = JsonNode.Parse(json)?.AsArray();
            if (volumes == null) return;

            var modified = false;

            foreach (var volume in volumes)
            {
                if (volume == null) continue;

                var volumeType = volume[KubernetesVolumePayloadProperties.Type]?.GetValue<string>();
                var refName = volume[KubernetesVolumePayloadProperties.ReferenceName]?.GetValue<string>();

                if (string.Equals(volumeType, KubernetesVolumeTypeValues.ConfigMap, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(refName, configMapBaseName, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(configMapBaseName))
                {
                    volume[KubernetesVolumePayloadProperties.ReferenceName] = $"{configMapBaseName}-{suffix}";
                    modified = true;
                }
                else if (string.Equals(volumeType, KubernetesVolumeTypeValues.Secret, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(refName, secretBaseName, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(secretBaseName))
                {
                    volume[KubernetesVolumePayloadProperties.ReferenceName] = $"{secretBaseName}-{suffix}";
                    modified = true;
                }
            }

            if (modified)
                properties[KubernetesProperties.CombinedVolumes] = volumes.ToJsonString();
        }
        catch { /* malformed JSON — skip rewriting */ }
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
