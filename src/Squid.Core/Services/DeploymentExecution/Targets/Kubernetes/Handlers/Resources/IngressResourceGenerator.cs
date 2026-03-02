using System.Text;
using System.Text.Json;

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
        var rulesJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressRules");

        if (string.IsNullOrWhiteSpace(rulesJson))
            return string.Empty;

        var tlsJson = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressTlsCertificates");
        var ingressClassName = KubernetesPropertyParser.GetProperty(properties, "Squid.Action.KubernetesContainers.IngressClassName");
        var annotations = KubernetesPropertyParser.ParseStringDictionaryProperty(properties, "Squid.Action.KubernetesContainers.IngressAnnotations");

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
                var host = rule.TryGetProperty("host", out var hostProp) ? hostProp.GetString() : null;
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

        if (rule.TryGetProperty("http", out var httpProp) && httpProp.TryGetProperty("paths", out pathsElement))
            hasPaths = true;
        else if (rule.TryGetProperty("paths", out pathsElement))
            hasPaths = true;

        if (!hasPaths || pathsElement.ValueKind != JsonValueKind.Array)
            return;

        sb.AppendLine("    http:");
        sb.AppendLine("      paths:");

        foreach (var path in pathsElement.EnumerateArray())
        {
            var pathStr = path.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : "/";
            var pathType = path.TryGetProperty("pathType", out var pathTypeProp) ? pathTypeProp.GetString() : "Prefix";
            sb.AppendLine($"      - path: {pathStr}");
            sb.AppendLine($"        pathType: {pathType}");
            sb.AppendLine("        backend:");

            if (path.TryGetProperty("backend", out var backend))
                AppendBackend(sb, backend);
        }
    }

    private static void AppendBackend(StringBuilder sb, JsonElement backend)
    {
        // Frontend flat format: { serviceName: "...", servicePort: "..." }
        if (backend.TryGetProperty("serviceName", out var serviceNameProp))
        {
            var serviceName = serviceNameProp.GetString();
            var servicePort = backend.TryGetProperty("servicePort", out var servicePortProp) ? servicePortProp.GetString() : null;
            sb.AppendLine("          service:");
            sb.AppendLine($"            name: {serviceName}");
            sb.AppendLine("            port:");

            if (int.TryParse(servicePort, out var portNumber))
                sb.AppendLine($"              number: {portNumber}");
            else if (!string.IsNullOrWhiteSpace(servicePort))
                sb.AppendLine($"              number: {servicePort}");
        }
        // K8s v1 format: { service: { name: "...", port: { number: 80 } } }
        else if (backend.TryGetProperty("service", out var service))
        {
            var serviceName = service.TryGetProperty("name", out var sNameProp) ? sNameProp.GetString() : null;
            sb.AppendLine("          service:");
            sb.AppendLine($"            name: {serviceName}");

            if (service.TryGetProperty("port", out var portObj) && portObj.TryGetProperty("number", out var numProp))
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
                var secretName = tls.TryGetProperty("secretName", out var secretNameProp) ? secretNameProp.GetString() : null;

                if (tls.TryGetProperty("hosts", out var hostsElement) && hostsElement.ValueKind == JsonValueKind.Array)
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
