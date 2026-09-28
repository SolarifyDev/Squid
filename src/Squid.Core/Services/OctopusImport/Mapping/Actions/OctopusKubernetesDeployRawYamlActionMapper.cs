using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusKubernetesDeployRawYamlActionMapper : IOctopusImportActionMapper
{
    private const string OctopusActionTypeName = "Octopus.KubernetesDeployRawYaml";
    private const string OctopusScriptSource = "Octopus.Action.Script.ScriptSource";
    private const string OctopusCustomResourceYaml = "Octopus.Action.KubernetesContainers.CustomResourceYaml";
    private const string OctopusCustomResourceYamlFileName = "Octopus.Action.KubernetesContainers.CustomResourceYamlFileName";
    private const string OctopusNamespace = "Octopus.Action.Kubernetes.Namespace";

    private static readonly IReadOnlySet<string> SupportedProperties = new HashSet<string>(
        [
            OctopusScriptSource,
            OctopusCustomResourceYaml,
            OctopusCustomResourceYamlFileName,
            OctopusNamespace,
            OctopusImportKubernetesActionMapperSupport.OctopusKubernetesContainersPrefix + "Namespace",
            OctopusPropertyNames.ActionPackageFeedId,
            OctopusPropertyNames.ActionPackageId,
            OctopusPropertyNames.ActionPackageVersion,
            OctopusImportPackageMapperSupport.LegacyNuGetFeedIdProperty,
            OctopusImportPackageMapperSupport.LegacyNuGetPackageIdProperty,
            OctopusImportKubernetesActionMapperSupport.OctopusResourceStatusCheck,
            OctopusImportKubernetesActionMapperSupport.OctopusDeploymentTimeout,
            KubernetesProperties.ServerSideApplyEnabled,
            KubernetesProperties.ServerSideApplyFieldManager,
            KubernetesProperties.ServerSideApplyForceConflicts
        ],
        StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => OctopusActionTypeName;

    public string SquidActionType => SpecialVariables.ActionTypes.KubernetesDeployRawYaml;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var model = OctopusImportKubernetesActionMapperSupport.CreateActionModel(action, SquidActionType);

        AddNamespace(action, model.Properties);
        AddYamlSource(action, model.Properties, diagnostics);
        OctopusImportPackageMapperSupport.AddPackageReferenceProperties(
            model.Properties,
            action,
            context,
            diagnostics,
            $"Octopus Kubernetes raw YAML action '{action.Name}'",
            missingFeedDiagnosticCode: OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping,
            diagnoseMultiplePackages: true,
            multiplePackagesDiagnosticCode: OctopusImportActionMappingDiagnosticCodes.MultiplePackageReferencesUnsupported);
        OctopusImportKubernetesActionMapperSupport.AddKubernetesExecutionProperties(action, model.Properties);
        OctopusImportKubernetesActionMapperSupport.AddUnsupportedPropertyDiagnostics(action, SupportedProperties, diagnostics);

        return new OctopusImportActionMappingResult(model, diagnostics);
    }

    private static void AddNamespace(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties)
    {
        OctopusImportPackageMapperSupport.AddMappedProperty(properties, action, OctopusNamespace, KubernetesProperties.Namespace);
        OctopusImportPackageMapperSupport.AddMappedProperty(
            properties,
            action,
            OctopusImportKubernetesActionMapperSupport.OctopusKubernetesContainersPrefix + "Namespace",
            KubernetesProperties.Namespace);
    }

    private static void AddYamlSource(
        OctopusDeploymentActionDto action,
        List<ActionPropertyModel> properties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var yaml = OctopusImportPackageMapperSupport.GetProperty(action, OctopusCustomResourceYaml);

        if (!string.IsNullOrWhiteSpace(yaml))
            OctopusImportPackageMapperSupport.AddProperty(properties, KubernetesRawYamlProperties.InlineYaml, yaml);

        var scriptSource = OctopusImportPackageMapperSupport.GetProperty(action, OctopusScriptSource);

        if (string.IsNullOrWhiteSpace(scriptSource))
            scriptSource = string.IsNullOrWhiteSpace(yaml) ? null : "Inline";

        if (string.Equals(scriptSource, "GitRepository", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSource,
                $"Octopus Kubernetes raw YAML action '{action.Name}' uses a Git repository source, which is not supported by the Squid raw YAML action.",
                action));
        }
        else if (!string.IsNullOrWhiteSpace(scriptSource)
                 && !string.Equals(scriptSource, "Inline", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(scriptSource, "Package", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSource,
                $"Octopus Kubernetes raw YAML action '{action.Name}' uses unsupported script source '{scriptSource}'.",
                action));
        }

        AddUnsupportedFileSelectionDiagnostic(action, diagnostics);
    }

    private static void AddUnsupportedFileSelectionDiagnostic(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var fileSelection = OctopusImportPackageMapperSupport.GetProperty(action, OctopusCustomResourceYamlFileName);

        if (string.IsNullOrWhiteSpace(fileSelection) || IsDefaultManifestSelection(fileSelection))
            return;

        diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.KubernetesYamlFileSelectionUnsupported,
            $"Octopus Kubernetes raw YAML action '{action.Name}' selects package files using '{fileSelection}'. Squid applies all YAML files from the package and cannot preserve this file selection filter.",
            action));
    }

    private static bool IsDefaultManifestSelection(string fileSelection)
    {
        var normalized = fileSelection.Replace('\\', '/').Trim();

        return string.Equals(normalized, "customresource.yml", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "customresource.yaml", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "./customresource.yml", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "./customresource.yaml", StringComparison.OrdinalIgnoreCase);
    }
}
