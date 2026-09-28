using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusTentaclePackageActionMapper : IOctopusImportActionMapper
{
    private const string OctopusActionTypeName = "Octopus.TentaclePackage";
    private const string OctopusCustomInstallationDirectory = "Octopus.Action.Package.CustomInstallationDirectory";

    private static readonly IReadOnlySet<string> SupportedProperties = new HashSet<string>(
        [
            OctopusPropertyNames.ActionPackageFeedId,
            OctopusPropertyNames.ActionPackageId,
            OctopusPropertyNames.ActionPackageVersion,
            OctopusCustomInstallationDirectory,
            OctopusImportPackageMapperSupport.LegacyNuGetFeedIdProperty,
            OctopusImportPackageMapperSupport.LegacyNuGetPackageIdProperty
        ],
        StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => OctopusActionTypeName;

    public string SquidActionType => SpecialVariables.ActionTypes.TentaclePackage;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var packageReference = OctopusImportPackageMapperSupport.ResolvePackageReference(
            action,
            OctopusImportPackageMapperSupport.LegacyNuGetFeedIdProperty,
            OctopusImportPackageMapperSupport.LegacyNuGetPackageIdProperty);
        var properties = new List<ActionPropertyModel>();

        if (packageReference == null)
        {
            diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted,
                $"Octopus package deployment action '{action.Name}' does not define a package reference.",
                action));
        }
        else
        {
            AddPackageReference(properties, action, context, diagnostics, packageReference);
        }

        AddInstallationDirectory(properties, action);
        OctopusImportKubernetesActionMapperSupport.AddUnsupportedPropertyDiagnostics(action, SupportedProperties, diagnostics);

        return new OctopusImportActionMappingResult(
            OctopusImportActionMapperHelper.CreateAction(action, SquidActionType, properties),
            diagnostics);
    }

    private static void AddPackageReference(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusImportPackageMapperSupport.PackageReference packageReference)
    {
        if (string.IsNullOrWhiteSpace(packageReference.PackageId))
        {
            diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted,
                $"Octopus package deployment action '{action.Name}' does not define a package id.",
                action));
        }
        else
        {
            OctopusImportPackageMapperSupport.AddProperty(properties, SpecialVariables.Action.PackageId, packageReference.PackageId);
        }

        if (!string.IsNullOrWhiteSpace(packageReference.Version))
            OctopusImportPackageMapperSupport.AddProperty(properties, SpecialVariables.Action.PackageVersion, packageReference.Version);

        if (string.IsNullOrWhiteSpace(packageReference.FeedId))
            return;

        if (OctopusImportPackageMapperSupport.TryResolveFeedId(
                packageReference.FeedId,
                context.IdMap,
                out var destinationFeedId))
        {
            OctopusImportPackageMapperSupport.AddProperty(properties, SpecialVariables.Action.PackageFeedId, destinationFeedId.ToString());
            return;
        }

        diagnostics.Add(OctopusImportPackageMapperSupport.Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping,
            $"Octopus package deployment action '{action.Name}' references package feed '{packageReference.FeedId}', which has not been mapped to a destination Squid feed.",
            action));
    }

    private static void AddInstallationDirectory(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action)
    {
        var customDirectory = OctopusImportPackageMapperSupport.GetProperty(action, OctopusCustomInstallationDirectory);
        var hasCustomDirectory = !string.IsNullOrWhiteSpace(customDirectory);

        OctopusImportPackageMapperSupport.AddProperty(
            properties,
            SpecialVariables.Action.InstallationDirectoryMode,
            hasCustomDirectory ? "Custom" : "Versioned");

        if (hasCustomDirectory)
            OctopusImportPackageMapperSupport.AddProperty(properties, SpecialVariables.Action.CustomInstallationDirectory, customDirectory);
    }
}
