using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class IngressResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var rulesJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressRules");

        return !string.IsNullOrWhiteSpace(rulesJson);
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var ingressName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressName");

        if (string.IsNullOrWhiteSpace(ingressName))
            ingressName = "ingress";

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var annotationsJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressAnnotations");
        var rulesJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressRules");

        if (string.IsNullOrWhiteSpace(rulesJson))
            return string.Empty;

        var tlsJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressTlsCertificates");
        var ingressClassName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressClassName");

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: networking.k8s.io/v1");
        sb.AppendLine("kind: Ingress");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {ingressName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        if (!string.IsNullOrWhiteSpace(annotationsJson))
            sb.AppendLine("  annotations: " + annotationsJson);

        sb.AppendLine("spec:");

        if (!string.IsNullOrWhiteSpace(ingressClassName))
            sb.AppendLine($"  ingressClassName: {ingressClassName}");

        sb.AppendLine("  rules: " + rulesJson);

        if (!string.IsNullOrWhiteSpace(tlsJson))
            sb.AppendLine("  tls: " + tlsJson);

        return sb.ToString();
    }
}
