using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class ServiceResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var serviceName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ServiceName");
        var ports = KubernetesPropertyParser.ParseServicePorts(properties);

        return !string.IsNullOrWhiteSpace(serviceName) && ports.Count > 0;
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var serviceName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ServiceName");

        if (string.IsNullOrWhiteSpace(serviceName))
            return string.Empty;

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var deploymentName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.DeploymentName");
        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, "Squid.Action.KubernetesContainers.DeploymentLabels");

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { ["app"] = string.IsNullOrWhiteSpace(deploymentName) ? serviceName : deploymentName };

        var serviceType = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.ServiceType");

        if (string.IsNullOrWhiteSpace(serviceType))
            serviceType = "ClusterIP";

        var ports = KubernetesPropertyParser.ParseServicePorts(properties);

        if (ports.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {serviceName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        sb.AppendLine("spec:");
        sb.AppendLine($"  type: {serviceType}");
        sb.AppendLine("  selector:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "    ", kvp.Key, kvp.Value);

        sb.AppendLine("  ports:");

        foreach (var port in ports)
        {
            sb.AppendLine("  - name: " + port.Name);
            sb.AppendLine($"    port: {port.Port}");

            if (port.TargetPort.HasValue)
                sb.AppendLine($"    targetPort: {port.TargetPort.Value}");

            if (port.NodePort.HasValue)
                sb.AppendLine($"    nodePort: {port.NodePort.Value}");

            if (!string.IsNullOrWhiteSpace(port.Protocol))
                sb.AppendLine($"    protocol: {port.Protocol}");
        }

        return sb.ToString();
    }
}
