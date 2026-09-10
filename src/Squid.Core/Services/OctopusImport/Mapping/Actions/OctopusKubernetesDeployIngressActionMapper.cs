using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusKubernetesDeployIngressActionMapper : IOctopusImportActionMapper
{
    private static readonly Dictionary<string, string> PropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Octopus.Action.KubernetesContainers.IngressName"] = KubernetesProperties.IngressName,
        ["Octopus.Action.KubernetesContainers.IngressClassName"] = KubernetesProperties.IngressClassName,
        ["Octopus.Action.KubernetesContainers.Namespace"] = KubernetesProperties.Namespace
    };

    private static readonly IReadOnlySet<string> SupportedProperties = new HashSet<string>(
        PropertyMap.Keys
            .Concat([
                "Octopus.Action.KubernetesContainers.IngressAnnotations",
                "Octopus.Action.KubernetesContainers.IngressRules",
                "Octopus.Action.KubernetesContainers.IngressTlsCertificates",
                OctopusImportKubernetesActionMapperSupport.OctopusResourceStatusCheck,
                OctopusImportKubernetesActionMapperSupport.OctopusDeploymentTimeout,
                "Octopus.Action.Kubernetes.ServerSideApply.Enabled",
                "Octopus.Action.Kubernetes.ServerSideApply.FieldManager",
                "Octopus.Action.Kubernetes.ServerSideApply.ForceConflicts"
            ]),
        StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => "Octopus.KubernetesDeployIngress";

    public string SquidActionType => SpecialVariables.ActionTypes.KubernetesDeployIngress;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var model = OctopusImportKubernetesActionMapperSupport.CreateActionModel(action, SquidActionType);

        OctopusImportKubernetesActionMapperSupport.AddSimpleMappedProperties(action, model.Properties, PropertyMap);
        OctopusImportKubernetesActionMapperSupport.AddKubernetesExecutionProperties(action, model.Properties);
        AddAnnotations(action, model.Properties, diagnostics);
        AddRules(action, model.Properties, diagnostics);
        AddTls(action, model.Properties, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddUnsupportedPropertyDiagnostics(action, SupportedProperties, diagnostics);

        return new OctopusImportActionMappingResult(model, diagnostics);
    }

    private static void AddAnnotations(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        const string sourceName = "Octopus.Action.KubernetesContainers.IngressAnnotations";

        if (!OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, sourceName, out var raw))
            return;

        var normalized = OctopusImportKubernetesActionMapperSupport.NormalizeStringDictionaryJson(raw, action, sourceName, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressAnnotations, normalized);
    }

    private static void AddRules(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        const string sourceName = "Octopus.Action.KubernetesContainers.IngressRules";

        if (!OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, sourceName, out var raw))
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, sourceName));
                OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressRules, raw);
                return;
            }

            var normalized = new JsonArray();

            foreach (var rule in doc.RootElement.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object)
                    continue;

                normalized.Add(NormalizeRule(rule));
            }

            OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressRules, normalized.ToJsonString());
        }
        catch (JsonException)
        {
            diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, sourceName));
            OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressRules, raw);
        }
    }

    private static JsonObject NormalizeRule(JsonElement source)
    {
        var rule = new JsonObject();
        var host = OctopusImportKubernetesActionMapperSupport.GetString(source, KubernetesIngressPayloadProperties.Host);

        if (host != null)
            rule[KubernetesIngressPayloadProperties.Host] = host;

        var paths = FindPaths(source);

        if (paths.ValueKind != JsonValueKind.Array)
            return rule;

        var normalizedPaths = new JsonArray();

        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Object)
                continue;

            normalizedPaths.Add(NormalizePath(path));
        }

        rule[KubernetesIngressPayloadProperties.Paths] = normalizedPaths;
        return rule;
    }

    private static JsonElement FindPaths(JsonElement rule)
    {
        if (rule.TryGetProperty(KubernetesIngressPayloadProperties.Http, out var http)
            && http.TryGetProperty(KubernetesIngressPayloadProperties.Paths, out var nestedPaths))
        {
            return nestedPaths;
        }

        if (rule.TryGetProperty(KubernetesIngressPayloadProperties.Paths, out var paths))
            return paths;

        return default;
    }

    private static JsonObject NormalizePath(JsonElement source)
    {
        if (source.TryGetProperty(KubernetesIngressPayloadProperties.Path, out _)
            || source.TryGetProperty(KubernetesIngressPayloadProperties.Backend, out _)
            || source.TryGetProperty(KubernetesIngressPayloadProperties.ServiceName, out _))
        {
            return OctopusImportKubernetesActionMapperSupport.Clone(source)?.AsObject() ?? new JsonObject();
        }

        var path = OctopusImportKubernetesActionMapperSupport.GetString(source, "key") ?? "/";
        var servicePort = OctopusImportKubernetesActionMapperSupport.GetString(source, "value");
        var serviceName = OctopusImportKubernetesActionMapperSupport.GetString(source, "option");
        var pathType = OctopusImportKubernetesActionMapperSupport.GetString(source, "option2");

        if (string.IsNullOrWhiteSpace(pathType))
            pathType = KubernetesIngressDefaultValues.PathType;

        var normalized = new JsonObject
        {
            [KubernetesIngressPayloadProperties.Path] = path,
            [KubernetesIngressPayloadProperties.PathType] = pathType
        };

        if (!string.IsNullOrWhiteSpace(serviceName) || !string.IsNullOrWhiteSpace(servicePort))
        {
            normalized[KubernetesIngressPayloadProperties.Backend] = new JsonObject
            {
                [KubernetesIngressPayloadProperties.ServiceName] = serviceName ?? string.Empty,
                [KubernetesIngressPayloadProperties.ServicePort] = servicePort ?? string.Empty
            };
        }

        return normalized;
    }

    private static void AddTls(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        const string sourceName = "Octopus.Action.KubernetesContainers.IngressTlsCertificates";

        if (!OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, sourceName, out var raw))
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, sourceName));
                OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressTlsCertificates, raw);
                return;
            }

            var normalized = new JsonArray();

            foreach (var tls in doc.RootElement.EnumerateArray())
            {
                if (tls.ValueKind != JsonValueKind.Object)
                    continue;

                var entry = new JsonObject();
                var secretName = OctopusImportKubernetesActionMapperSupport.GetString(tls, KubernetesIngressPayloadProperties.SecretName);

                if (!string.IsNullOrWhiteSpace(secretName))
                    entry[KubernetesIngressPayloadProperties.SecretName] = secretName;

                if (tls.TryGetProperty(KubernetesIngressPayloadProperties.Hosts, out var hosts) && hosts.ValueKind == JsonValueKind.Array)
                    entry[KubernetesIngressPayloadProperties.Hosts] = OctopusImportKubernetesActionMapperSupport.Clone(hosts);

                normalized.Add(entry);
            }

            OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressTlsCertificates, normalized.ToJsonString());
        }
        catch (JsonException)
        {
            diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, sourceName));
            OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.IngressTlsCertificates, raw);
        }
    }
}
