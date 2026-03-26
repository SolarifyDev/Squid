using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class DeploymentResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
        => KubernetesPropertyParser.ParseContainers(properties).Count > 0;

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

        var deploymentStrategy = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentStyle);

        if (string.IsNullOrWhiteSpace(deploymentStrategy))
            deploymentStrategy = KubernetesDeploymentStrategyValues.RollingUpdate;

        var deploymentAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentAnnotations);
        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { [KubernetesLabelKeys.App] = deploymentName };

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: Deployment");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {YamlSafeScalar.Escape(deploymentName)}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {YamlSafeScalar.Escape(namespaceName)}");

        PodTemplateYamlBuilder.AppendDictionary(sb, "  annotations:", "    ", deploymentAnnotations);
        PodTemplateYamlBuilder.AppendDictionary(sb, "  labels:", "    ", deploymentLabels);

        sb.AppendLine("spec:");
        sb.AppendLine($"  replicas: {replicas}");
        PodTemplateYamlBuilder.AppendIntPropertyIfPresent(sb, "  ", "revisionHistoryLimit", properties, KubernetesProperties.RevisionHistoryLimit);
        PodTemplateYamlBuilder.AppendIntPropertyIfPresent(sb, "  ", "progressDeadlineSeconds", properties, KubernetesProperties.ProgressDeadlineSeconds);

        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "      ", kvp.Key, kvp.Value);

        var k8sStrategyType = string.Equals(deploymentStrategy, KubernetesDeploymentStrategyValues.Recreate, StringComparison.OrdinalIgnoreCase)
            ? KubernetesDeploymentStrategyValues.Recreate
            : KubernetesDeploymentStrategyValues.RollingUpdate;

        sb.AppendLine("  strategy:");
        sb.AppendLine($"    type: {k8sStrategyType}");
        AppendRollingUpdateIfPresent(sb, k8sStrategyType, properties);

        PodTemplateYamlBuilder.AppendPodTemplate(sb, properties, selectorLabels, "  ");

        return sb.ToString();
    }

    private static void AppendRollingUpdateIfPresent(StringBuilder sb, string deploymentStrategy, Dictionary<string, string> properties)
    {
        if (!string.Equals(deploymentStrategy, KubernetesDeploymentStrategyValues.RollingUpdate, StringComparison.OrdinalIgnoreCase))
            return;

        var maxUnavailable = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.MaxUnavailable);
        var maxSurge = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.MaxSurge);

        if (string.IsNullOrWhiteSpace(maxUnavailable) && string.IsNullOrWhiteSpace(maxSurge))
            return;

        sb.AppendLine("    rollingUpdate:");
        KubernetesPropertyParser.AppendIntOrStringValue(sb, "      ", "maxUnavailable", maxUnavailable);
        KubernetesPropertyParser.AppendIntOrStringValue(sb, "      ", "maxSurge", maxSurge);
    }
}
