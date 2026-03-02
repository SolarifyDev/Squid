using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class ConfigMapResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var configMapName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ConfigMapName);

        if (string.IsNullOrWhiteSpace(configMapName))
            return false;

        var values = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.ConfigMapValues);

        return values.Count > 0;
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var configMapName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ConfigMapName);

        if (string.IsNullOrWhiteSpace(configMapName))
            return string.Empty;

        var values = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.ConfigMapValues);

        if (values.Count == 0)
            return string.Empty;

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {configMapName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        sb.AppendLine("data:");

        foreach (var kvp in values)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                sb.AppendLine($"  {kvp.Key}: \"\"");
                continue;
            }

            if (kvp.Value.Contains('\n', StringComparison.Ordinal))
            {
                var indented = kvp.Value.Replace("\r\n", "\n", StringComparison.Ordinal);
                indented = indented.Replace("\n", "\n    ", StringComparison.Ordinal);

                sb.AppendLine($"  {kvp.Key}: |");
                sb.AppendLine("    " + indented);
            }
            else
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        return sb.ToString();
    }
}
