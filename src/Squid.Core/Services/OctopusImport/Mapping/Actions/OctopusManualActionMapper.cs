using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusManualActionMapper : IOctopusImportActionMapper
{
    private const string OctopusActionTypeName = "Octopus.Manual";
    private const string OctopusInstructionsPropertyName = "Octopus.Action.Manual.Instructions";
    private const string OctopusResponsibleTeamIdsPropertyName = "Octopus.Action.Manual.ResponsibleTeamIds";

    public string OctopusActionType => OctopusActionTypeName;

    public string SquidActionType => SpecialVariables.ActionTypes.Manual;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var properties = new List<ActionPropertyModel>();

        AddMappedProperty(properties, action.Properties, OctopusInstructionsPropertyName, SpecialVariables.Action.ManualInstructions);
        AddResponsibleTeams(properties, action, context, diagnostics);

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

    private static void AddResponsibleTeams(
        List<ActionPropertyModel> properties,
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var sourceTeamIds = SplitReferenceList(GetProperty(action.Properties, OctopusResponsibleTeamIdsPropertyName)).ToList();

        if (sourceTeamIds.Count == 0)
            return;

        var destinationTeamIds = new List<int>();

        foreach (var sourceTeamId in sourceTeamIds)
        {
            if (context.IdMap.TryGetDestinationId(sourceTeamId, OctopusResourceKind.Team.ToString(), out var destinationTeamId))
            {
                destinationTeamIds.Add(destinationTeamId);
                continue;
            }

            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportActionMappingDiagnosticCodes.MissingResponsibleTeamMapping,
                $"Octopus manual action '{action.Name}' references responsible team '{sourceTeamId}', which has not been mapped to a destination Squid team.",
                action));
        }

        if (destinationTeamIds.Count > 0)
        {
            properties.Add(Property(
                SpecialVariables.Action.ManualResponsibleTeamIds,
                string.Join(",", destinationTeamIds.Distinct())));
        }
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

    private static IEnumerable<string> SplitReferenceList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v));
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
