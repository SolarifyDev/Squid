using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class JobResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var resourceType = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentResourceType);

        return string.Equals(resourceType, KubernetesResourceTypeValues.Job, StringComparison.OrdinalIgnoreCase)
               && KubernetesPropertyParser.ParseContainers(properties).Count > 0;
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var deploymentName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);
        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var replicasText = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.Replicas);

        var containerSpecs = KubernetesPropertyParser.ParseContainers(properties);

        if (containerSpecs.Count == 0)
            return string.Empty;

        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);
        var deploymentAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentAnnotations);

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { [KubernetesLabelKeys.App] = deploymentName };

        // For Jobs, Replicas maps to completions
        int? completions = null;

        if (!string.IsNullOrWhiteSpace(replicasText) && int.TryParse(replicasText, out var parsed) && parsed >= 0)
            completions = parsed;

        // Force restartPolicy to Never for Jobs (default is Always which is invalid for Jobs)
        var jobProperties = new Dictionary<string, string>(properties);

        if (!jobProperties.TryGetValue(KubernetesProperties.PodRestartPolicy, out var existingPolicy)
            || string.IsNullOrWhiteSpace(existingPolicy)
            || string.Equals(existingPolicy, KubernetesPodDefaultValues.RestartPolicyAlways, StringComparison.OrdinalIgnoreCase))
        {
            jobProperties[KubernetesProperties.PodRestartPolicy] = "Never";
        }

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: batch/v1");
        sb.AppendLine("kind: Job");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {YamlSafeScalar.Escape(deploymentName)}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {YamlSafeScalar.Escape(namespaceName)}");

        PodTemplateYamlBuilder.AppendDictionary(sb, "  annotations:", "    ", deploymentAnnotations);
        PodTemplateYamlBuilder.AppendDictionary(sb, "  labels:", "    ", deploymentLabels);

        sb.AppendLine("spec:");

        if (completions.HasValue)
            sb.AppendLine($"  completions: {completions.Value}");

        sb.AppendLine("  backoffLimit: 6");

        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", kvp.Key, kvp.Value);

        PodTemplateYamlBuilder.AppendPodTemplate(sb, jobProperties, selectorLabels, "  ");

        return sb.ToString();
    }
}
