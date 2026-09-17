using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

internal static class OctopusImportPackageMapperSupport
{
    internal const string LegacyNuGetFeedIdProperty = "Octopus.Action.Package.NuGetFeedId";
    internal const string LegacyNuGetPackageIdProperty = "Octopus.Action.Package.NuGetPackageId";

    internal static PackageReference ResolvePackageReference(
        OctopusDeploymentActionDto action,
        string legacyFeedProperty = null,
        string legacyPackageProperty = null)
    {
        var firstPackage = action.Packages?.FirstOrDefault();

        if (firstPackage != null)
        {
            return new PackageReference(
                firstPackage.PackageId,
                firstPackage.FeedId,
                firstPackage.Version);
        }

        var packageId = GetProperty(action, OctopusPropertyNames.ActionPackageId)
                        ?? GetProperty(action, legacyPackageProperty);
        var feedId = GetProperty(action, OctopusPropertyNames.ActionPackageFeedId)
                     ?? GetProperty(action, legacyFeedProperty);
        var version = GetProperty(action, OctopusPropertyNames.ActionPackageVersion);

        if (string.IsNullOrWhiteSpace(packageId)
            && string.IsNullOrWhiteSpace(feedId)
            && string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return new PackageReference(packageId, feedId, version);
    }

    internal static void AddPackageReferenceProperties(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        List<OctopusImportDiagnosticDto> diagnostics,
        string actionDescription,
        string missingFeedDiagnosticCode = OctopusImportActionMappingDiagnosticCodes.MissingPackageFeedMapping,
        bool diagnoseMultiplePackages = false,
        string multiplePackagesDiagnosticCode = OctopusImportActionMappingDiagnosticCodes.MultiplePackageReferencesUnsupported,
        string legacyFeedProperty = null,
        string legacyPackageProperty = null)
    {
        var packageReference = ResolvePackageReference(action, legacyFeedProperty, legacyPackageProperty);

        if (packageReference == null)
            return;

        if (diagnoseMultiplePackages && (action.Packages?.Count ?? 0) > 1)
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                multiplePackagesDiagnosticCode,
                $"{actionDescription} contains multiple package references. Squid currently supports one action-level package reference for this action.",
                action));
        }

        AddPackageReferenceProperties(
            properties,
            action,
            context,
            diagnostics,
            packageReference,
            actionDescription,
            missingFeedDiagnosticCode);
    }

    internal static void AddMappedProperty(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        string sourceName,
        string destinationName)
    {
        var value = GetProperty(action, sourceName);

        if (!string.IsNullOrWhiteSpace(value))
            AddProperty(properties, destinationName, value);
    }

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

    internal static string GetProperty(OctopusDeploymentActionDto action, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || action?.Properties == null)
            return null;

        if (action.Properties.TryGetValue(name, out var value))
            return value;

        return action.Properties
            .FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

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

    private static void AddPackageReferenceProperties(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        List<OctopusImportDiagnosticDto> diagnostics,
        PackageReference packageReference,
        string actionDescription,
        string missingFeedDiagnosticCode)
    {
        if (!string.IsNullOrWhiteSpace(packageReference.PackageId))
            AddProperty(properties, SpecialVariables.Action.PackageId, packageReference.PackageId);

        if (!string.IsNullOrWhiteSpace(packageReference.Version))
            AddProperty(properties, SpecialVariables.Action.PackageVersion, packageReference.Version);

        if (string.IsNullOrWhiteSpace(packageReference.FeedId))
            return;

        if (TryResolveFeedId(
                packageReference.FeedId,
                context.IdMap,
                out var destinationFeedId))
        {
            AddProperty(properties, SpecialVariables.Action.PackageFeedId, destinationFeedId.ToString());
            return;
        }

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            missingFeedDiagnosticCode,
            $"{actionDescription} references package feed '{packageReference.FeedId}', which has not been mapped to a destination Squid feed.",
            action));
    }

    internal static bool TryResolveFeedId(
        string sourceFeedId,
        OctopusImportIdMap idMap,
        out int destinationFeedId)
    {
        destinationFeedId = default;

        if (string.IsNullOrWhiteSpace(sourceFeedId))
            return false;

        return idMap.TryGetDestinationId(
            sourceFeedId,
            OctopusResourceKind.Feed.ToString(),
            out destinationFeedId);
    }

    internal sealed record PackageReference(string PackageId, string FeedId, string Version);
}
