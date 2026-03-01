using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class ConfigMapResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var configMapName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ConfigMapName");
        var configValues = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ConfigMapValues");

        if (string.IsNullOrWhiteSpace(configMapName) || string.IsNullOrWhiteSpace(configValues))
            return false;

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(configValues) ?? new Dictionary<string, string>();
            return values.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var configMapName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ConfigMapName");
        var configValues = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ConfigMapValues");

        if (string.IsNullOrWhiteSpace(configMapName) || string.IsNullOrWhiteSpace(configValues))
            return string.Empty;

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);

        Dictionary<string, string> values;

        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(configValues) ?? new Dictionary<string, string>();
        }
        catch
        {
            values = new Dictionary<string, string>();
        }

        if (values.Count == 0)
            return string.Empty;

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
