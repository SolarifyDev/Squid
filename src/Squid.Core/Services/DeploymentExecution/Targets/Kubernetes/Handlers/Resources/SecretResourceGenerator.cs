using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class SecretResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var secretName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.SecretName");
        var secretValues = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.SecretValues");

        if (string.IsNullOrWhiteSpace(secretName) || string.IsNullOrWhiteSpace(secretValues))
            return false;

        try
        {
            var values = ParseSecretValues(secretValues);
            return values.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var secretName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.SecretName");
        var secretValuesJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.SecretValues");

        if (string.IsNullOrWhiteSpace(secretName) || string.IsNullOrWhiteSpace(secretValuesJson))
            return string.Empty;

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        Dictionary<string, string> values;

        try
        {
            values = ParseSecretValues(secretValuesJson);
        }
        catch
        {
            values = new Dictionary<string, string>();
        }

        if (values.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {secretName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        sb.AppendLine("type: Opaque");
        sb.AppendLine("stringData:");

        foreach (var kvp in values)
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseSecretValues(string json)
    {
        var result = new Dictionary<string, string>();

        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var key = element.TryGetProperty("Key", out var keyProp) ? keyProp.GetString()
                    : element.TryGetProperty("key", out var lowerKeyProp) ? lowerKeyProp.GetString()
                    : null;

                var value = element.TryGetProperty("Value", out var valueProp) ? valueProp.GetString() ?? string.Empty
                    : element.TryGetProperty("value", out var lowerValueProp) ? lowerValueProp.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(key))
                    result[key] = value;
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(property.Name))
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }
}
