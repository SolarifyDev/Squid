using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

/// <summary>
/// Octopus' "Deploy Kubernetes containers" step keeps the properties of an optional resource
/// (Service, Ingress, ConfigMap, Secret, Custom resource) even after the feature is switched off —
/// Octopus simply does not deploy them. Squid's generator has no feature switch: it renders every
/// resource whose properties exist. Importing a disabled feature's leftovers would therefore deploy
/// resources Octopus never deployed, so they are dropped here (with an Info diagnostic).
/// When <c>Octopus.Action.EnabledFeatures</c> is absent nothing is gated (older exports).
/// </summary>
internal static class OctopusKubernetesContainersFeatures
{
    private const string Prefix = "Octopus.Action.KubernetesContainers.";

    private static readonly IReadOnlyList<FeatureGroup> Groups =
    [
        new("Octopus.Features.KubernetesService", "Service", key => HasSuffixPrefix(key, "Service") && !HasSuffixPrefix(key, "ServiceAccount")),
        new("Octopus.Features.KubernetesIngress", "Ingress", key => HasSuffixPrefix(key, "Ingress")),
        new("Octopus.Features.KubernetesConfigMap", "ConfigMap", key => HasSuffixPrefix(key, "ConfigMap")),
        new("Octopus.Features.KubernetesSecret", "Secret", key => HasSuffixPrefix(key, "Secret")),
        new("Octopus.Features.KubernetesCustomResource", "Custom resource", key => HasSuffixPrefix(key, "CustomResource"))
    ];

    public static OctopusDeploymentActionDto RemoveDisabledFeatureProperties(OctopusDeploymentActionDto action, List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (action?.Properties == null || !TryGetRawProperty(action.Properties, OctopusPropertyNames.ActionEnabledFeatures, out var rawFeatures)) return action;

        var enabled = ParseFeatures(rawFeatures);

        OctopusKubernetesContainersLinkedResources.AddDanglingReferenceDiagnostics(action, enabled, diagnostics);

        var disabledKeys = CollectDisabledKeys(action, enabled, diagnostics);

        if (disabledKeys.Count == 0) return action;

        return WithProperties(action, KeepProperties(action.Properties, disabledKeys));
    }

    // Keeps the source dictionary's own comparer: the export DTO is case-sensitive, so two keys that
    // differ only by case are both legal there and must both survive (re-keying case-insensitively
    // would throw). Lookups downstream are case-insensitive and take the first match in source order.
    private static Dictionary<string, string> KeepProperties(Dictionary<string, string> properties, HashSet<string> disabledKeys)
        => properties.Where(p => !disabledKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, properties.Comparer);

    private static HashSet<string> CollectDisabledKeys(OctopusDeploymentActionDto action, HashSet<string> enabled, List<OctopusImportDiagnosticDto> diagnostics)
    {
        var disabledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Groups.Where(g => !enabled.Contains(g.FeatureId)))
        {
            var keys = action.Properties.Keys.Where(group.Owns).Where(k => !string.IsNullOrWhiteSpace(action.Properties[k])).ToList();

            if (keys.Count == 0) continue;

            disabledKeys.UnionWith(keys);
            diagnostics.Add(OctopusImportKubernetesActionMapperSupport.Diagnostic(
                OctopusImportCompatibilitySeverity.Info,
                OctopusImportActionMappingDiagnosticCodes.DisabledFeaturePropertiesOmitted,
                $"Octopus action '{action.Name}' has {group.DisplayName} settings but the feature is not enabled, so Octopus did not deploy them; they were not imported.",
                action));
        }

        return disabledKeys;
    }

    // Present-but-empty is meaningful here ("no optional features enabled"), so this deliberately
    // does not use the shared TryGetProperty, which treats an empty value as absent.
    private static bool TryGetRawProperty(Dictionary<string, string> properties, string name, out string value)
    {
        var match = properties.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));

        value = match.Value;

        return match.Key != null;
    }

    private static HashSet<string> ParseFeatures(string raw)
        => (raw ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool HasSuffixPrefix(string key, string suffixPrefix)
        => key.StartsWith(Prefix + suffixPrefix, StringComparison.OrdinalIgnoreCase);

    private static OctopusDeploymentActionDto WithProperties(OctopusDeploymentActionDto action, Dictionary<string, string> properties)
    {
        // Never mutate the graph's source DTO — the graph is re-read by preview, validation and confirmation.
        var copy = JsonSerializer.Deserialize<OctopusDeploymentActionDto>(JsonSerializer.Serialize(action));
        copy.Properties = properties;
        return copy;
    }

    private sealed record FeatureGroup(string FeatureId, string DisplayName, Func<string, bool> Owns);
}
