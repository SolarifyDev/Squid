using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusHelmChartUpgradeActionMapper : IOctopusImportActionMapper
{
    private const string OctopusActionTypeName = "Octopus.HelmChartUpgrade";
    private const string OctopusReleaseName = "Octopus.Action.Helm.ReleaseName";
    private const string OctopusNamespace = "Octopus.Action.Helm.Namespace";
    private const string OctopusChartDirectory = "Octopus.Action.Helm.ChartDirectory";
    private const string OctopusCustomHelmExecutable = "Octopus.Action.Helm.CustomHelmExecutable";
    private const string OctopusResetValues = "Octopus.Action.Helm.ResetValues";
    private const string OctopusAdditionalArgs = "Octopus.Action.Helm.AdditionalArgs";
    private const string OctopusYamlValues = "Octopus.Action.Helm.YamlValues";
    private const string OctopusKeyValues = "Octopus.Action.Helm.KeyValues";
    private const string OctopusTimeout = "Octopus.Action.Helm.Timeout";
    private const string OctopusClientVersion = "Octopus.Action.Helm.ClientVersion";
    private const string OctopusTemplateValuesSources = "Octopus.Action.Helm.TemplateValuesSources";

    private static readonly IReadOnlySet<string> SupportedProperties = new HashSet<string>(
        [
            OctopusReleaseName,
            OctopusNamespace,
            OctopusChartDirectory,
            OctopusCustomHelmExecutable,
            OctopusResetValues,
            OctopusAdditionalArgs,
            OctopusYamlValues,
            OctopusKeyValues,
            OctopusTimeout,
            OctopusClientVersion,
            OctopusTemplateValuesSources,
            OctopusPropertyNames.ActionPackageFeedId,
            OctopusPropertyNames.ActionPackageId,
            OctopusPropertyNames.ActionPackageVersion
        ],
        StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => OctopusActionTypeName;

    public string SquidActionType => SpecialVariables.ActionTypes.HelmChartUpgrade;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var model = OctopusImportKubernetesActionMapperSupport.CreateActionModel(action, SquidActionType);

        AddSimpleProperties(action, model.Properties);
        OctopusImportPackageMapperSupport.AddPackageReferenceProperties(
            model.Properties,
            action,
            context,
            diagnostics,
            $"Octopus Helm action '{action.Name}'",
            missingFeedDiagnosticCode: OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping,
            diagnoseMultiplePackages: true,
            multiplePackagesDiagnosticCode: OctopusImportActionMappingDiagnosticCodes.MultiplePackageReferencesUnsupported);
        AddTemplateValuesSources(action, model.Properties, diagnostics);
        AddUnsupportedSourceDiagnostics(action, diagnostics);
        OctopusImportKubernetesActionMapperSupport.AddUnsupportedPropertyDiagnostics(action, SupportedProperties, diagnostics);

        return new OctopusImportActionMappingResult(model, diagnostics);
    }

    private static void AddSimpleProperties(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties)
    {
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusReleaseName, KubernetesHelmProperties.ReleaseName);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusNamespace, KubernetesProperties.Namespace);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusChartDirectory, KubernetesHelmProperties.ChartPath);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusCustomHelmExecutable, KubernetesHelmProperties.CustomHelmExecutable);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusResetValues, KubernetesHelmProperties.ResetValues);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusAdditionalArgs, KubernetesHelmProperties.AdditionalArgs);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusYamlValues, KubernetesHelmProperties.YamlValues);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusKeyValues, KubernetesHelmProperties.KeyValues);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusTimeout, KubernetesHelmProperties.Timeout);
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusClientVersion, KubernetesHelmProperties.ClientVersion);
    }

    private static void AddTemplateValuesSources(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var raw = OctopusImportPackageMapperSupport.GetProperty(action, OctopusTemplateValuesSources);

        if (string.IsNullOrWhiteSpace(raw))
            return;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, OctopusTemplateValuesSources));
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(OctopusImportKubernetesActionMapperSupport.MalformedJsonDiagnostic(action, OctopusTemplateValuesSources));
                return;
            }

            var normalized = new JsonArray();

            foreach (var source in document.RootElement.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                    continue;

                var type = OctopusImportKubernetesActionMapperSupport.GetString(source, "Type");

                if (string.Equals(type, "InlineYaml", StringComparison.OrdinalIgnoreCase))
                {
                    var value = OctopusImportKubernetesActionMapperSupport.GetString(source, "Value");

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        normalized.Add(new JsonObject
                        {
                            ["Type"] = "InlineYaml",
                            ["Value"] = value
                        });
                    }

                    continue;
                }

                if (string.Equals(type, "KeyValues", StringComparison.OrdinalIgnoreCase))
                {
                    var value = NormalizeKeyValues(source, diagnostics, action);

                    if (value != null)
                    {
                        normalized.Add(new JsonObject
                        {
                            ["Type"] = "KeyValues",
                            ["Value"] = value.ToJsonString()
                        });
                    }

                    continue;
                }

                diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    OctopusImportActionMappingDiagnosticCodes.UnsupportedHelmValueSource,
                    $"Octopus Helm action '{action.Name}' uses template values source '{type}', which is not supported by Squid. Only InlineYaml and KeyValues sources can be imported.",
                    action));
            }

            if (normalized.Count > 0)
                OctopusImportPackageMapperSupport.AddProperty(properties, KubernetesHelmProperties.ValueSources, normalized.ToJsonString());
        }
    }

    private static JsonNode NormalizeKeyValues(
        JsonElement source,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusDeploymentActionDto action)
    {
        if (!source.TryGetProperty("Value", out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Object)
            return NormalizeKeyValuesObject(value, diagnostics, action);

        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonObject();

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var key = OctopusImportKubernetesActionMapperSupport.GetString(item, "Key")
                          ?? OctopusImportKubernetesActionMapperSupport.GetString(item, "key");
                var itemValue = GetRawValue(item, "Value")
                                ?? GetRawValue(item, "value");

                if (!string.IsNullOrWhiteSpace(key) && itemValue != null)
                    result[key] = itemValue;
            }

            return result.Count > 0 ? result : null;
        }

        diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.UnsupportedHelmValueSource,
            $"Octopus Helm action '{action.Name}' contains a KeyValues template source with an unsupported value shape.",
            action));
        return null;
    }

    private static JsonNode NormalizeKeyValuesObject(
        JsonElement value,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusDeploymentActionDto action)
    {
        var result = new JsonObject();

        foreach (var property in value.EnumerateObject())
        {
            var normalized = NormalizeKeyValue(property.Value);

            if (normalized == null)
            {
                diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    OctopusImportActionMappingDiagnosticCodes.UnsupportedHelmValueSource,
                    $"Octopus Helm action '{action.Name}' contains KeyValues entry '{property.Name}' with an unsupported value shape.",
                    action));
                continue;
            }

            result[property.Name] = normalized;
        }

        return result.Count > 0 ? result : null;
    }

    private static JsonNode NormalizeKeyValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(value.GetString()),
            JsonValueKind.Number => JsonValue.Create(value.GetRawText()),
            JsonValueKind.True => JsonValue.Create("True"),
            JsonValueKind.False => JsonValue.Create("False"),
            _ => null
        };

    private static JsonNode GetRawValue(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value))
            return null;

        return NormalizeKeyValue(value);
    }

    private static void AddUnsupportedSourceDiagnostics(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if ((action.GitDependencies?.Count ?? 0) == 0)
            return;

        diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSource,
            $"Octopus Helm action '{action.Name}' depends on Git repository content, which is not supported for Squid Helm chart deployments.",
            action));
    }
}
