using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusScriptActionMapper : IOctopusImportActionMapper
{
    private const string OctopusActionTypeName = "Octopus.Script";
    private const string OctopusScriptBodyPropertyName = "Octopus.Action.Script.ScriptBody";
    private const string OctopusScriptSyntaxPropertyName = "Octopus.Action.Script.Syntax";
    private const string OctopusScriptSourcePropertyName = "Octopus.Action.Script.ScriptSource";
    private const string OctopusPackageFeedIdPropertyName = "Octopus.Action.Package.FeedId";
    private const string OctopusPackageIdPropertyName = "Octopus.Action.Package.PackageId";
    private const string OctopusPackageVersionPropertyName = "Octopus.Action.Package.PackageVersion";

    private static readonly IReadOnlySet<string> SupportedSyntaxes =
        Enum.GetNames<ScriptSyntax>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string OctopusActionType => OctopusActionTypeName;

    public string SquidActionType => SpecialVariables.ActionTypes.Script;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var properties = new List<ActionPropertyModel>();

        AddMappedProperty(properties, action.Properties, OctopusScriptSourcePropertyName, SpecialVariables.Action.ScriptSource);
        AddMappedSyntax(properties, action, diagnostics);
        AddMappedProperty(properties, action.Properties, OctopusScriptBodyPropertyName, SpecialVariables.Action.ScriptBody);
        AddPackageReferenceProperties(properties, action, context, diagnostics);

        var model = new CreateOrUpdateDeploymentActionModel
        {
            Name = action.Name,
            ActionType = SquidActionType,
            IsDisabled = action.IsDisabled,
            IsRequired = action.IsRequired,
            CanBeUsedForProjectVersioning = false,
            Properties = properties
        };

        return new OctopusImportActionMappingResult(model, diagnostics);
    }

    private static void AddMappedSyntax(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var syntax = GetProperty(action.Properties, OctopusScriptSyntaxPropertyName);

        if (string.IsNullOrWhiteSpace(syntax))
            return;

        if (!SupportedSyntaxes.Contains(syntax.Trim()))
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.UnsupportedScriptSyntax,
                $"Octopus script action '{action.Name}' uses syntax '{syntax}', which is not supported by Squid script actions.",
                action));
            return;
        }

        properties.Add(Property(SpecialVariables.Action.ScriptSyntax, syntax.Trim()));
    }

    private static void AddPackageReferenceProperties(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var packageReference = ResolvePackageReference(action);

        if (packageReference == null)
            return;

        if ((action.Packages?.Count ?? 0) > 1)
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.MultiplePackageReferencesUnsupported,
                $"Octopus script action '{action.Name}' contains multiple package references. Squid script action import currently supports one action-level package reference.",
                action));
        }

        if (!string.IsNullOrWhiteSpace(packageReference.PackageId))
            properties.Add(Property(SpecialVariables.Action.PackageId, packageReference.PackageId));

        if (!string.IsNullOrWhiteSpace(packageReference.Version))
            properties.Add(Property(SpecialVariables.Action.PackageVersion, packageReference.Version));

        if (string.IsNullOrWhiteSpace(packageReference.FeedId))
            return;

        if (context.IdMap.TryGetDestinationId(packageReference.FeedId, OctopusResourceKind.Feed.ToString(), out var destinationFeedId))
        {
            properties.Add(Property(SpecialVariables.Action.PackageFeedId, destinationFeedId.ToString()));
            return;
        }

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportActionMappingDiagnosticCodes.MissingPackageFeedMapping,
            $"Octopus script action '{action.Name}' references package feed '{packageReference.FeedId}', which has not been mapped to a destination Squid feed.",
            action));
    }

    private static PackageReference ResolvePackageReference(OctopusDeploymentActionDto action)
    {
        var firstPackage = action.Packages?.FirstOrDefault();

        if (firstPackage != null)
        {
            return new PackageReference(
                firstPackage.PackageId,
                firstPackage.FeedId,
                firstPackage.Version);
        }

        var packageId = GetProperty(action.Properties, OctopusPackageIdPropertyName);
        var feedId = GetProperty(action.Properties, OctopusPackageFeedIdPropertyName);
        var version = GetProperty(action.Properties, OctopusPackageVersionPropertyName);

        if (string.IsNullOrWhiteSpace(packageId)
            && string.IsNullOrWhiteSpace(feedId)
            && string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return new PackageReference(packageId, feedId, version);
    }

    private static void AddMappedProperty(
        List<ActionPropertyModel> properties,
        Dictionary<string, string> source,
        string sourceName,
        string destinationName)
    {
        var value = GetProperty(source, sourceName);

        if (!string.IsNullOrWhiteSpace(value))
            properties.Add(Property(destinationName, value));
    }

    private static string GetProperty(Dictionary<string, string> source, string name)
    {
        if (source == null)
            return null;

        return source.TryGetValue(name, out var value)
            ? value
            : source.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static ActionPropertyModel Property(string name, string value)
        => new()
        {
            PropertyName = name,
            PropertyValue = value
        };

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusDeploymentActionDto action)
        => new()
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
            SourceId = action.Id,
            ResourceName = action.Name
        };

    private sealed record PackageReference(string PackageId, string FeedId, string Version);
}
