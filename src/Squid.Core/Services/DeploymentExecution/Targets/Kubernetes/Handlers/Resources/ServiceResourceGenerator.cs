using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class ServiceResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var serviceName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceName);
        var ports = KubernetesPropertyParser.ParseServicePorts(properties);

        return !string.IsNullOrWhiteSpace(serviceName) && ports.Count > 0;
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var serviceName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceName);

        if (string.IsNullOrWhiteSpace(serviceName))
            return string.Empty;

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var deploymentName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.DeploymentName);
        var deploymentLabels = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.DeploymentLabels);

        var selectorLabels = deploymentLabels.Count > 0
            ? deploymentLabels
            : new Dictionary<string, string> { ["app"] = string.IsNullOrWhiteSpace(deploymentName) ? serviceName : deploymentName };

        var serviceType = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceType);

        if (string.IsNullOrWhiteSpace(serviceType))
            serviceType = "ClusterIP";

        var serviceClusterIp = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.ServiceClusterIp);
        var serviceAnnotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.ServiceAnnotations);
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

        if (serviceAnnotations.Count > 0)
        {
            sb.AppendLine("  annotations:");

            foreach (var kvp in serviceAnnotations)
                sb.AppendLine($"    {kvp.Key}: {kvp.Value}");
        }

        sb.AppendLine("spec:");
        sb.AppendLine($"  type: {serviceType}");

        if (!string.IsNullOrWhiteSpace(serviceClusterIp))
            sb.AppendLine($"  clusterIP: {serviceClusterIp}");
        sb.AppendLine("  selector:");

        foreach (var kvp in selectorLabels)
            KubernetesPropertyParser.AppendKeyValueIfNotNullOrWhiteSpace(sb, "    ", kvp.Key, kvp.Value);

        sb.AppendLine("  ports:");

        foreach (var port in ports)
        {
            sb.AppendLine("  - name: " + port.Name);
            sb.AppendLine($"    port: {port.Port}");

            if (!string.IsNullOrWhiteSpace(port.TargetPort))
                sb.AppendLine($"    targetPort: {port.TargetPort}");

            if (port.NodePort.HasValue)
                sb.AppendLine($"    nodePort: {port.NodePort.Value}");

            if (!string.IsNullOrWhiteSpace(port.Protocol))
                sb.AppendLine($"    protocol: {port.Protocol}");
        }

        return sb.ToString();
    }
}
