using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class StatefulSetResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var resourceType = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentResourceType);

        return string.Equals(resourceType, KubernetesResourceTypeValues.StatefulSet, StringComparison.OrdinalIgnoreCase)
               && KubernetesPropertyParser.ParseContainers(properties).Count > 0;
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var deploymentName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);
        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var replicasText = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.Replicas);

        int replicas = 1;

        if (!string.IsNullOrWhiteSpace(replicasText) && int.TryParse(replicasText, out var parsed) && parsed >= 0)
            replicas = parsed;

        var containerSpecs = KubernetesPropertyParser.ParseContainers(properties);

        if (containerSpecs.Count == 0)
            return string.Empty;

        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);
        var deploymentAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentAnnotations);

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { [KubernetesLabelKeys.App] = deploymentName };

        var serviceName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceName);

        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = deploymentName;

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: StatefulSet");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {YamlSafeScalar.Escape(deploymentName)}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {YamlSafeScalar.Escape(namespaceName)}");

        PodTemplateYamlBuilder.AppendDictionary(sb, "  annotations:", "    ", deploymentAnnotations);
        PodTemplateYamlBuilder.AppendDictionary(sb, "  labels:", "    ", deploymentLabels);

        sb.AppendLine("spec:");
        sb.AppendLine($"  serviceName: {YamlSafeScalar.Escape(serviceName)}");
        sb.AppendLine($"  replicas: {replicas}");

        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", kvp.Key, kvp.Value);

        PodTemplateYamlBuilder.AppendPodTemplate(sb, properties, selectorLabels, "  ");

        return sb.ToString();
    }
}
