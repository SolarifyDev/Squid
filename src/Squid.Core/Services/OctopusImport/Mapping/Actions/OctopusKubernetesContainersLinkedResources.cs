using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

/// <summary>
/// A pod can mount (LinkedResource volume) or read (envFrom / env valueFrom) the step's own ConfigMap
/// or Secret. When that feature is switched off neither Octopus nor Squid deploys the resource, so
/// such a reference points at nothing — a LinkedResource volume even renders with no source and
/// Kubernetes silently mounts an emptyDir. The import keeps Octopus' behaviour and warns instead.
/// </summary>
internal static class OctopusKubernetesContainersLinkedResources
{
    private const string Prefix = OctopusImportKubernetesActionMapperSupport.OctopusKubernetesContainersPrefix;

    private static readonly IReadOnlyList<LinkedResource> Resources =
    [
        new("Octopus.Features.KubernetesConfigMap", "ConfigMap", KubernetesVolumeTypeValues.ConfigMap, Prefix + "ConfigMapName", KubernetesContainerPayloadProperties.ConfigMapEnvFromSource, KubernetesContainerPayloadProperties.ConfigMapEnvironmentVariables),
        new("Octopus.Features.KubernetesSecret", "Secret", KubernetesVolumeTypeValues.Secret, Prefix + "SecretName", KubernetesContainerPayloadProperties.SecretEnvFromSource, KubernetesContainerPayloadProperties.SecretEnvironmentVariables)
    ];

    public static void AddDanglingReferenceDiagnostics(OctopusDeploymentActionDto action, HashSet<string> enabledFeatures, List<OctopusImportDiagnosticDto> diagnostics)
    {
        foreach (var resource in Resources.Where(r => !enabledFeatures.Contains(r.FeatureId)))
            AddDiagnostic(action, resource, FindReferrers(action, resource), diagnostics);
    }

    private static List<string> FindReferrers(OctopusDeploymentActionDto action, LinkedResource resource)
    {
        OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, resource.NameProperty, out var name);

        return FindVolumeReferrers(action, resource).Concat(FindContainerReferrers(action, resource, name?.Trim())).ToList();
    }

    private static IEnumerable<string> FindVolumeReferrers(OctopusDeploymentActionDto action, LinkedResource resource)
        => ReadArray(action, Prefix + "CombinedVolumes")
            .Where(v => Matches(v, KubernetesVolumePayloadProperties.Type, resource.VolumeType) && Matches(v, KubernetesVolumePayloadProperties.ResourceNameMode, KubernetesVolumeResourceNameModes.LinkedResource))
            .Select(v => $"volume {GetString(v, KubernetesVolumePayloadProperties.Name)}");

    private static IEnumerable<string> FindContainerReferrers(OctopusDeploymentActionDto action, LinkedResource resource, string name)
    {
        if (string.IsNullOrEmpty(name)) return [];

        return ReadArray(action, Prefix + "Containers")
            .Where(c => ReferencesName(c, resource.EnvFromProperty, KubernetesContainerEnvFromPayloadProperties.Name, name) || ReferencesName(c, resource.EnvVarProperty, KubernetesContainerEnvVarSourcePayloadProperties.Value, name))
            .Select(c => $"container {GetString(c, KubernetesContainerPayloadProperties.Name)}");
    }

    private static bool ReferencesName(JsonElement container, string arrayProperty, string nameProperty, string name)
        => TryGetProperty(container, arrayProperty, out var items) && items.ValueKind == JsonValueKind.Array && items.EnumerateArray().Any(i => Matches(i, nameProperty, name));

    private static void AddDiagnostic(OctopusDeploymentActionDto action, LinkedResource resource, List<string> referrers, List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (referrers.Count == 0) return;

        diagnostics.Add(OctopusImportKubernetesActionMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportActionMappingDiagnosticCodes.DisabledFeatureResourceReferenced,
            // Names stay unquoted and no apostrophes are used: redaction blanks any '...' span holding words like secret.
            $"Octopus action '{action.Name}' does not enable the {resource.DisplayName} feature, so the {resource.DisplayName} of this step is not deployed, yet {string.Join(", ", referrers)} still reference it. Point them at an existing {resource.DisplayName} or enable the feature before deploying.",
            action));
    }

    private static List<JsonElement> ReadArray(OctopusDeploymentActionDto action, string property)
    {
        if (!OctopusImportKubernetesActionMapperSupport.TryGetProperty(action, property, out var raw)) return [];

        try
        {
            using var document = JsonDocument.Parse(raw);

            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList() : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Matches(JsonElement element, string property, string expected)
        => string.Equals(GetString(element, property)?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        var match = element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));

        value = match.Value;

        return match.Value.ValueKind != JsonValueKind.Undefined;
    }

    private sealed record LinkedResource(string FeatureId, string DisplayName, string VolumeType, string NameProperty, string EnvFromProperty, string EnvVarProperty);
}
