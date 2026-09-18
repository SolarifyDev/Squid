using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
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

        AddMappedProperty(properties, action.Properties, OctopusPropertyNames.ActionScriptSource, SpecialVariables.Action.ScriptSource);
        AddMappedSyntax(properties, action, diagnostics);
        AddMappedProperty(properties, action.Properties, OctopusPropertyNames.ActionScriptBody, SpecialVariables.Action.ScriptBody);
        OctopusImportPackageMapperSupport.AddPackageReferenceProperties(
            properties,
            action,
            context,
            diagnostics,
            $"Octopus script action '{action.Name}'",
            diagnoseMultiplePackages: true);

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
        var syntax = GetProperty(action.Properties, OctopusPropertyNames.ActionScriptSyntax);

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
