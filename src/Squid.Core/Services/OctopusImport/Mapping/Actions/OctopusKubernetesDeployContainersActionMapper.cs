using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusKubernetesDeployContainersActionMapper : IOctopusImportActionMapper
{
    private static readonly Dictionary<string, string> PropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Octopus.Action.KubernetesContainers.DeploymentResourceType"] = KubernetesProperties.DeploymentResourceType,
        ["Octopus.Action.KubernetesContainers.DeploymentName"] = KubernetesProperties.DeploymentName,
        ["Octopus.Action.KubernetesContainers.Namespace"] = KubernetesProperties.Namespace,
        ["Octopus.Action.KubernetesContainers.Replicas"] = KubernetesProperties.Replicas,
        ["Octopus.Action.KubernetesContainers.RevisionHistoryLimit"] = KubernetesProperties.RevisionHistoryLimit,
        ["Octopus.Action.KubernetesContainers.ProgressDeadlineSeconds"] = KubernetesProperties.ProgressDeadlineSeconds,
        ["Octopus.Action.KubernetesContainers.PodTerminationGracePeriodSeconds"] = KubernetesProperties.PodTerminationGracePeriodSeconds,
        ["Octopus.Action.KubernetesContainers.PodPriorityClassName"] = KubernetesProperties.PodPriorityClassName,
        ["Octopus.Action.KubernetesContainers.PodRestartPolicy"] = KubernetesProperties.PodRestartPolicy,
        ["Octopus.Action.KubernetesContainers.PodDnsPolicy"] = KubernetesProperties.PodDnsPolicy,
        ["Octopus.Action.KubernetesContainers.PodDnsNameservers"] = KubernetesProperties.PodDnsNameservers,
        ["Octopus.Action.KubernetesContainers.PodDnsSearches"] = KubernetesProperties.PodDnsSearches,
        ["Octopus.Action.KubernetesContainers.PodReadinessGates"] = KubernetesProperties.PodReadinessGates,
        ["Octopus.Action.KubernetesContainers.ServiceAccountName"] = KubernetesProperties.ServiceAccountName,
        ["Octopus.Action.KubernetesContainers.PodHostNetworking"] = KubernetesProperties.PodHostNetworking,
        ["Octopus.Action.KubernetesContainers.PodSecurityFsGroup"] = KubernetesProperties.PodSecurityFsGroup,
        ["Octopus.Action.KubernetesContainers.PodSecurityRunAsGroup"] = KubernetesProperties.PodSecurityRunAsGroup,
        ["Octopus.Action.KubernetesContainers.PodSecurityRunAsUser"] = KubernetesProperties.PodSecurityRunAsUser,
        ["Octopus.Action.KubernetesContainers.PodSecuritySupplementalGroups"] = KubernetesProperties.PodSecuritySupplementalGroups,
        ["Octopus.Action.KubernetesContainers.PodSecurityRunAsNonRoot"] = KubernetesProperties.PodSecurityRunAsNonRoot,
        ["Octopus.Action.KubernetesContainers.PodSecuritySeLinuxLevel"] = KubernetesProperties.PodSecuritySeLinuxLevel,
        ["Octopus.Action.KubernetesContainers.PodSecuritySeLinuxRole"] = KubernetesProperties.PodSecuritySeLinuxRole,
        ["Octopus.Action.KubernetesContainers.PodSecuritySeLinuxType"] = KubernetesProperties.PodSecuritySeLinuxType,
        ["Octopus.Action.KubernetesContainers.PodSecuritySeLinuxUser"] = KubernetesProperties.PodSecuritySeLinuxUser,
        ["Octopus.Action.KubernetesContainers.DeploymentStyle"] = KubernetesProperties.DeploymentStyle,
        ["Octopus.Action.KubernetesContainers.BlueGreenActiveSlot"] = KubernetesProperties.BlueGreenActiveSlot,
        ["Octopus.Action.KubernetesContainers.MaxUnavailable"] = KubernetesProperties.MaxUnavailable,
        ["Octopus.Action.KubernetesContainers.MaxSurge"] = KubernetesProperties.MaxSurge,
        ["Octopus.Action.KubernetesContainers.DeploymentLabels"] = KubernetesProperties.DeploymentLabels,
        ["Octopus.Action.KubernetesContainers.DeploymentAnnotations"] = KubernetesProperties.DeploymentAnnotations,
        ["Octopus.Action.KubernetesContainers.PodAnnotations"] = KubernetesProperties.PodAnnotations,
        ["Octopus.Action.KubernetesContainers.CombinedVolumes"] = KubernetesProperties.CombinedVolumes,
        ["Octopus.Action.KubernetesContainers.ServiceName"] = KubernetesProperties.ServiceName,
        ["Octopus.Action.KubernetesContainers.ServiceType"] = KubernetesProperties.ServiceType,
        ["Octopus.Action.KubernetesContainers.ServiceClusterIp"] = KubernetesProperties.ServiceClusterIp,
        ["Octopus.Action.KubernetesContainers.ServiceAnnotations"] = KubernetesProperties.ServiceAnnotations,
        ["Octopus.Action.KubernetesContainers.ServicePorts"] = KubernetesProperties.ServicePorts,
        ["Octopus.Action.KubernetesContainers.ConfigMapName"] = KubernetesProperties.ConfigMapName,
        ["Octopus.Action.KubernetesContainers.SecretName"] = KubernetesProperties.SecretName,
        ["Octopus.Action.KubernetesContainers.ObjectStatusCheck"] = KubernetesProperties.ObjectStatusCheck,
        ["Octopus.Action.KubernetesContainers.ObjectStatusCheckTimeout"] = KubernetesProperties.ObjectStatusCheckTimeout,
        ["Octopus.Action.KubernetesContainers.HostAliases"] = KubernetesProperties.HostAliases,
        ["Octopus.Action.KubernetesContainers.Tolerations"] = KubernetesProperties.Tolerations,
        ["Octopus.Action.KubernetesContainers.NodeAffinity"] = KubernetesProperties.NodeAffinity,
        ["Octopus.Action.KubernetesContainers.PodAffinity"] = KubernetesProperties.PodAffinity,
        ["Octopus.Action.KubernetesContainers.PodAntiAffinity"] = KubernetesProperties.PodAntiAffinity,
        ["Octopus.Action.KubernetesContainers.PodSecurityImagePullSecrets"] = KubernetesProperties.PodSecurityImagePullSecrets,
        ["Octopus.Action.KubernetesContainers.PodSecuritySysctls"] = KubernetesProperties.PodSecuritySysctls,
        ["Octopus.Action.KubernetesContainers.DnsConfigOptions"] = KubernetesProperties.DnsConfigOptions
    };

    private static readonly IReadOnlySet<string> SupportedProperties = new HashSet<string>(
        PropertyMap.Keys
            .Concat([
                "Octopus.Action.KubernetesContainers.Containers",
                "Octopus.Action.KubernetesContainers.ConfigMapValues",
                "Octopus.Action.KubernetesContainers.SecretValues",
                OctopusImportKubernetesActionMapperSupport.OctopusResourceStatusCheck,
                OctopusImportKubernetesActionMapperSupport.OctopusDeploymentTimeout,
                "Octopus.Action.Kubernetes.ServerSideApply.Enabled",
                "Octopus.Action.Kubernetes.ServerSideApply.FieldManager",
                "Octopus.Action.Kubernetes.ServerSideApply.ForceConflicts"
            ]),
        StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => "Octopus.KubernetesDeployContainers";

    public string SquidActionType => SpecialVariables.ActionTypes.KubernetesDeployContainers;

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
        AddNormalizedJsonProperty(action, model.Properties, diagnostics, "Octopus.Action.KubernetesContainers.ConfigMapValues", KubernetesProperties.ConfigMapValues, diagnoseSensitiveConfigMapValues: true);
        AddNormalizedJsonProperty(action, model.Properties, diagnostics, "Octopus.Action.KubernetesContainers.SecretValues", KubernetesProperties.SecretValues);
        AddNormalizedContainers(action, context, model.Properties, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddUnsupportedPropertyDiagnostics(action, SupportedProperties, diagnostics);

        return new OctopusImportActionMappingResult(model, diagnostics);
    }

    private static void AddNormalizedJsonProperty(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics,
        string sourceName,
        string destinationName,
        bool diagnoseSensitiveConfigMapValues = false)
    {
        if (!OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, sourceName, out var raw))
            return;

        var normalized = OctopusImportKubernetesActionMapperSupport.NormalizeStringDictionaryJson(raw, action, sourceName, diagnostics);
        if (diagnoseSensitiveConfigMapValues)
            AddSensitiveConfigMapValueDiagnostics(action, sourceName, normalized, diagnostics);

        OctopusImportKubernetesActionMapperSupport.AddProperty(properties, destinationName, normalized);
    }

    private static void AddSensitiveConfigMapValueDiagnostics(
        OctopusDeploymentActionDto action,
        string sourceName,
        string normalized,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var suspiciousEntryCount = CountSuspiciousStringDictionaryEntries(normalized);
        if (suspiciousEntryCount == 0)
            return;

        diagnostics.Add(OctopusImportKubernetesActionMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportActionMappingDiagnosticCodes.SensitiveConfigMapValue,
            $"Octopus Kubernetes ConfigMap property '{sourceName}' on action '{action.Name}' contains {suspiciousEntryCount} value(s) that look sensitive. Move secret or sensitive values to Kubernetes Secret values before relying on the imported action.",
            action));
    }

    private static int CountSuspiciousStringDictionaryEntries(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Object => doc.RootElement.EnumerateObject()
                    .Count(property => OctopusImportRedaction.ShouldRedactPropertyValue(property.Name, GetString(property.Value))),
                JsonValueKind.Array => doc.RootElement.EnumerateArray()
                    .Count(IsSuspiciousKeyValueElement),
                _ => 0
            };
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static bool IsSuspiciousKeyValueElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var key = OctopusImportKubernetesActionMapperSupport.GetString(element, "Key")
                  ?? OctopusImportKubernetesActionMapperSupport.GetString(element, "key");
        var value = OctopusImportKubernetesActionMapperSupport.GetString(element, "Value")
                    ?? OctopusImportKubernetesActionMapperSupport.GetString(element, "value");

        return OctopusImportRedaction.ShouldRedactPropertyValue(key, value);
    }

    private static void AddNormalizedContainers(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        const string sourceName = "Octopus.Action.KubernetesContainers.Containers";

        if (!OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, sourceName, out var raw))
            return;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, sourceName));
                OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.Containers, raw);
                return;
            }

            var normalized = new JsonArray();

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var container = OctopusImportKubernetesActionMapperSupport.Clone(element)?.AsObject() ?? new JsonObject();
                NormalizeContainer(action, context, container, diagnostics);
                normalized.Add(container);
            }

            OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.Containers, normalized.ToJsonString());
        }
        catch (JsonException)
        {
            diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, sourceName));
            OctopusImportKubernetesActionMapperSupport.AddProperty(properties, KubernetesProperties.Containers, raw);
        }
    }

    private static void NormalizeContainer(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        JsonObject container,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var containerName = container[KubernetesContainerPayloadProperties.Name]?.GetValue<string>();
        var package = FindPackage(action, containerName);

        if (container[KubernetesContainerPayloadProperties.PackageId] == null
            && !string.IsNullOrWhiteSpace(package?.PackageId))
        {
            container[KubernetesContainerPayloadProperties.PackageId] = package.PackageId;
        }

        var sourceFeedId = GetString(container[KubernetesContainerPayloadProperties.FeedId]);

        if (string.IsNullOrWhiteSpace(sourceFeedId))
            sourceFeedId = package?.FeedId;

        if (string.IsNullOrWhiteSpace(sourceFeedId))
            return;

        if (OctopusImportKubernetesActionMapperSupport.TryResolveFeedId(sourceFeedId, context.IdMap, out var destinationFeedId))
        {
            container[KubernetesContainerPayloadProperties.FeedId] = destinationFeedId;
            return;
        }

        diagnostics.Add(OctopusImportKubernetesActionMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping,
            $"Octopus Kubernetes container '{containerName ?? string.Empty}' on action '{action.Name}' references feed '{sourceFeedId}', which has not been mapped to a destination Squid feed.",
            action));
    }

    private static OctopusActionPackageDto FindPackage(OctopusDeploymentActionDto action, string containerName)
    {
        var packages = action.Packages ?? [];

        if (!string.IsNullOrWhiteSpace(containerName))
        {
            var named = packages.FirstOrDefault(p => string.Equals(p.Name, containerName, StringComparison.OrdinalIgnoreCase));

            if (named != null)
                return named;
        }

        return packages.Count == 1 ? packages[0] : null;
    }

    private static string GetString(JsonNode node)
        => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(),
            _ => null
        };

    private static string GetString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null
        };
}
