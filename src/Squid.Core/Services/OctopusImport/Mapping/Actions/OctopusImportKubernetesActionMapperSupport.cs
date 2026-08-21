using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

internal static class OctopusImportKubernetesActionMapperSupport
{
    internal const string OctopusKubernetesContainersPrefix = "Octopus.Action.KubernetesContainers.";
    internal const string OctopusKubernetesPrefix = "Octopus.Action.Kubernetes.";
    internal const string OctopusResourceStatusCheck = "Octopus.Action.Kubernetes.ResourceStatusCheck";
    internal const string OctopusDeploymentTimeout = "Octopus.Action.Kubernetes.DeploymentTimeout";
    internal const string OctopusEnabledFeatures = "Octopus.Action.EnabledFeatures";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static CreateOrUpdateDeploymentActionModel CreateActionModel(OctopusDeploymentActionDto action, string squidActionType)
        => new()
        {
            Name = action.Name,
            ActionType = squidActionType,
            IsDisabled = action.IsDisabled,
            IsRequired = action.IsRequired,
            CanBeUsedForProjectVersioning = false,
            Properties = []
        };

    internal static void AddProperty(List<ActionPropertyModel> properties, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            return;

        properties.Add(new ActionPropertyModel
        {
            PropertyName = name,
            PropertyValue = value
        });
    }

    internal static bool TryGetProperty(OctopusDeploymentActionDto action, string sourceName, out string value)
    {
        value = null;

        if (action.Properties == null)
            return false;

        foreach (var property in action.Properties)
        {
            if (!string.Equals(property.Key, sourceName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    internal static void AddSimpleMappedProperties(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        IReadOnlyDictionary<string, string> propertyMap)
    {
        foreach (var (sourceName, destinationName) in propertyMap)
        {
            if (TryGetProperty(action, sourceName, out var value))
                AddProperty(properties, destinationName, value);
        }
    }

    internal static void AddKubernetesExecutionProperties(OctopusDeploymentActionDto action, List<ActionPropertyModel> properties)
    {
        if (TryGetProperty(action, OctopusResourceStatusCheck, out var statusCheck))
            AddProperty(properties, KubernetesProperties.ObjectStatusCheck, statusCheck);

        if (TryGetProperty(action, OctopusDeploymentTimeout, out var timeout))
            AddProperty(properties, KubernetesProperties.ObjectStatusCheckTimeout, timeout);

        foreach (var property in action.Properties ?? [])
        {
            if (!property.Key.StartsWith(OctopusKubernetesPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(property.Key, OctopusResourceStatusCheck, StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Key, OctopusDeploymentTimeout, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = property.Key[OctopusKubernetesPrefix.Length..];
            var destinationName = $"Squid.Action.Kubernetes.{suffix}";
            AddProperty(properties, destinationName, property.Value);
        }
    }

    internal static void AddUnsupportedPropertyDiagnostics(
        OctopusDeploymentActionDto action,
        IReadOnlySet<string> supportedProperties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        foreach (var property in action.Properties ?? [])
        {
            if (string.IsNullOrWhiteSpace(property.Value))
                continue;

            if (supportedProperties.Contains(property.Key)
                || string.Equals(property.Key, OctopusEnabledFeatures, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsStepLevelActionProperty(property.Key) || IsEmptyJson(property.Value))
                continue;

            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportActionMappingDiagnosticCodes.UnsupportedProperty,
                $"Octopus action property '{property.Key}' for action '{action.Name}' is not supported by the Kubernetes import action mapper and was omitted.",
            action));
        }
    }

    private static bool IsStepLevelActionProperty(string propertyName)
        => string.Equals(propertyName, "Octopus.Action.TargetRoles", StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, "Octopus.Action.RunOnServer", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(value);

            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.GetArrayLength() == 0,
                JsonValueKind.Object => !doc.RootElement.EnumerateObject().Any(),
                JsonValueKind.Null or JsonValueKind.Undefined => true,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    internal static string NormalizeStringDictionaryJson(
        string raw,
        OctopusDeploymentActionDto action,
        string sourcePropertyName,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return raw;

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return raw;

            var normalized = new JsonArray();

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var key = GetString(element, "Key") ?? GetString(element, "key");
                var value = GetString(element, "Value") ?? GetString(element, "value");

                if (string.IsNullOrWhiteSpace(key) || value == null)
                    continue;

                normalized.Add(new JsonObject
                {
                    ["Key"] = key,
                    ["Value"] = value
                });
            }

            return normalized.ToJsonString();
        }
        catch (JsonException)
        {
            diagnostics.Add(MalformedJsonDiagnostic(action, sourcePropertyName));
            return raw;
        }
    }

    internal static bool TryResolveFeedId(string sourceFeedId, OctopusImportIdMap idMap, out int destinationFeedId)
    {
        destinationFeedId = default;

        if (string.IsNullOrWhiteSpace(sourceFeedId))
            return false;

        if (int.TryParse(sourceFeedId, out destinationFeedId) && destinationFeedId > 0)
            return true;

        if (idMap.TryGetDestinationId(sourceFeedId, OctopusResourceKind.Feed.ToString(), out destinationFeedId))
            return true;

        var sourcePrefix = sourceFeedId.Trim();
        var matches = idMap.Mappings
            .Where(m => string.Equals(m.SourceType, OctopusResourceKind.Feed.ToString(), StringComparison.OrdinalIgnoreCase)
                        && m.SourceId.StartsWith($"{sourcePrefix}-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            destinationFeedId = matches[0].DestinationId;
            return true;
        }

        return false;
    }

    internal static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null
        };
    }

    internal static JsonNode Clone(JsonElement element)
        => JsonNode.Parse(element.GetRawText());

    internal static OctopusImportDiagnosticDto MalformedJsonDiagnostic(
        OctopusDeploymentActionDto action,
        string sourcePropertyName)
        => Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.MalformedEmbeddedJson,
            $"Octopus action property '{sourcePropertyName}' for action '{action.Name}' contains malformed embedded JSON.",
            action);

    internal static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusDeploymentActionDto action)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
            SourceId = action.Id,
            ResourceName = action.Name
        });
}
