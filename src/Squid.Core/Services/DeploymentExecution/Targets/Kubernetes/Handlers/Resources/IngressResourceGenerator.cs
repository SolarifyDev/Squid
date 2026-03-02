using System.Text;
using System.Text.Json;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal sealed class IngressResourceGenerator : IKubernetesResourceGenerator
{
    public bool CanGenerate(Dictionary<string, string> properties)
    {
        var rulesJson = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.IngressRules);

        return !string.IsNullOrWhiteSpace(rulesJson);
    }

    public string Generate(Dictionary<string, string> properties)
    {
        var ingressName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.IngressName);

        if (string.IsNullOrWhiteSpace(ingressName))
            ingressName = KubernetesIngressDefaultValues.Name;

        var namespaceName = KubernetesPropertyParser.GetNamespace(properties);
        var rulesJson = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.IngressRules);

        if (string.IsNullOrWhiteSpace(rulesJson))
            return string.Empty;

        var tlsJson = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.IngressTlsCertificates);
        var ingressClassName = KubernetesPropertyParser.GetProperty(properties, KubernetesProperties.IngressClassName);
        var annotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, KubernetesProperties.IngressAnnotations);

        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: networking.k8s.io/v1");
        sb.AppendLine("kind: Ingress");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {ingressName}");

        if (!string.IsNullOrWhiteSpace(namespaceName))
            sb.AppendLine($"  namespace: {namespaceName}");

        if (annotations.Count > 0)
        {
            sb.AppendLine("  annotations:");

            foreach (var kvp in annotations)
                sb.AppendLine($"    {kvp.Key}: {kvp.Value}");
        }

        sb.AppendLine("spec:");

        if (!string.IsNullOrWhiteSpace(ingressClassName))
            sb.AppendLine($"  ingressClassName: {ingressClassName}");

        AppendRules(sb, rulesJson);

        if (!string.IsNullOrWhiteSpace(tlsJson))
            AppendTls(sb, tlsJson);

        return sb.ToString();
    }

    private static void AppendRules(StringBuilder sb, string rulesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rulesJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            sb.AppendLine("  rules:");

            foreach (var rule in doc.RootElement.EnumerateArray())
            {
                var host = rule.TryGetProperty(KubernetesIngressPayloadProperties.Host, out var hostProp) ? hostProp.GetString() : null;
                sb.AppendLine($"  - host: {host ?? string.Empty}");
                AppendRuleHttp(sb, rule);
            }
        }
        catch { }
    }

    private static void AppendRuleHttp(StringBuilder sb, JsonElement rule)
    {
        // Support both K8s v1 format (http.paths) and frontend flat format (paths at root)
        var pathsElement = default(JsonElement);
        var hasPaths = false;

        if (rule.TryGetProperty(KubernetesIngressPayloadProperties.Http, out var httpProp) && httpProp.TryGetProperty(KubernetesIngressPayloadProperties.Paths, out pathsElement))
            hasPaths = true;
        else if (rule.TryGetProperty(KubernetesIngressPayloadProperties.Paths, out pathsElement))
            hasPaths = true;

        if (!hasPaths || pathsElement.ValueKind != JsonValueKind.Array)
            return;

        sb.AppendLine("    http:");
        sb.AppendLine("      paths:");

        foreach (var path in pathsElement.EnumerateArray())
        {
            var pathStr = path.TryGetProperty(KubernetesIngressPayloadProperties.Path, out var pathProp) ? pathProp.GetString() : KubernetesIngressDefaultValues.Path;
            var pathType = path.TryGetProperty(KubernetesIngressPayloadProperties.PathType, out var pathTypeProp) ? pathTypeProp.GetString() : KubernetesIngressDefaultValues.PathType;
            sb.AppendLine($"      - path: {pathStr}");
            sb.AppendLine($"        pathType: {pathType}");
            sb.AppendLine("        backend:");

            if (path.TryGetProperty(KubernetesIngressPayloadProperties.Backend, out var backend))
                AppendBackend(sb, backend);
            else if (path.TryGetProperty(KubernetesIngressPayloadProperties.ServiceName, out _))
                AppendBackend(sb, path);
        }
    }

    private static void AppendBackend(StringBuilder sb, JsonElement backend)
    {
        // Frontend flat format: { serviceName: "...", servicePort: "..." or 80 }
        if (backend.TryGetProperty(KubernetesIngressPayloadProperties.ServiceName, out var serviceNameProp))
        {
            var serviceName = serviceNameProp.GetString();
            var servicePort = (string)null;

            if (backend.TryGetProperty(KubernetesIngressPayloadProperties.ServicePort, out var servicePortProp))
                servicePort = servicePortProp.ValueKind == JsonValueKind.Number
                    ? servicePortProp.GetRawText()
                    : servicePortProp.GetString();

            sb.AppendLine("          service:");
            sb.AppendLine($"            name: {serviceName}");
            sb.AppendLine("            port:");

            if (!string.IsNullOrWhiteSpace(servicePort))
                sb.AppendLine($"              number: {servicePort}");
        }
        // K8s v1 format: { service: { name: "...", port: { number: 80 } } }
        else if (backend.TryGetProperty(KubernetesIngressPayloadProperties.Service, out var service))
        {
            var serviceName = service.TryGetProperty(KubernetesIngressPayloadProperties.Name, out var sNameProp) ? sNameProp.GetString() : null;
            sb.AppendLine("          service:");
            sb.AppendLine($"            name: {serviceName}");

            if (service.TryGetProperty(KubernetesIngressPayloadProperties.Port, out var portObj) && portObj.TryGetProperty(KubernetesIngressPayloadProperties.Number, out var numProp))
            {
                sb.AppendLine("            port:");
                sb.AppendLine($"              number: {numProp.GetRawText()}");
            }
        }
    }

    private static void AppendTls(StringBuilder sb, string tlsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(tlsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            sb.AppendLine("  tls:");

            foreach (var tls in doc.RootElement.EnumerateArray())
            {
                var secretName = tls.TryGetProperty(KubernetesIngressPayloadProperties.SecretName, out var secretNameProp) ? secretNameProp.GetString() : null;

                if (tls.TryGetProperty(KubernetesIngressPayloadProperties.Hosts, out var hostsElement) && hostsElement.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine("  - hosts:");

                    foreach (var host in hostsElement.EnumerateArray())
                        sb.AppendLine($"    - {host.GetString()}");
                }
                else
                {
                    sb.AppendLine("  -");
                }

                if (!string.IsNullOrWhiteSpace(secretName))
                    sb.AppendLine($"    secretName: {secretName}");
            }
        }
        catch { }
    }
}
