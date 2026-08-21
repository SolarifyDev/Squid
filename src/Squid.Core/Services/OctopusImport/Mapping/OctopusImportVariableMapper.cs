using System.Globalization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportVariableMapper : IScopedDependency
{
    OctopusImportVariableMappingResult MapToCreateCommand(
        OctopusResourceNode variableSetResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId,
        string name = null,
        string description = null);

    OctopusImportVariableMappingResult MapToUpdateCommand(
        OctopusResourceNode variableSetResource,
        OctopusImportIdMap idMap,
        int destinationVariableSetId,
        int destinationSpaceId,
        string name = null,
        string description = null);
}

public class OctopusImportVariableMapper : IOctopusImportVariableMapper
{
    public OctopusImportVariableMappingResult MapToCreateCommand(
        OctopusResourceNode variableSetResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId,
        string name = null,
        string description = null)
    {
        var mapping = Map(variableSetResource, idMap, destinationSpaceId, name, description);

        return new OctopusImportVariableMappingResult(
            new CreateVariableSetCommand
            {
                Name = mapping.Name,
                Description = mapping.Description,
                OwnerId = mapping.OwnerId,
                OwnerType = mapping.OwnerType,
                SpaceId = destinationSpaceId,
                Variables = mapping.Variables
            },
            null,
            mapping.RequiredInputs,
            mapping.Diagnostics);
    }

    public OctopusImportVariableMappingResult MapToUpdateCommand(
        OctopusResourceNode variableSetResource,
        OctopusImportIdMap idMap,
        int destinationVariableSetId,
        int destinationSpaceId,
        string name = null,
        string description = null)
    {
        if (destinationVariableSetId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationVariableSetId), destinationVariableSetId, "Destination variable set id must be positive.");

        var mapping = Map(variableSetResource, idMap, destinationSpaceId, name, description);

        return new OctopusImportVariableMappingResult(
            null,
            new UpdateVariableSetCommand
            {
                Id = destinationVariableSetId,
                Name = mapping.Name,
                Description = mapping.Description,
                OwnerId = mapping.OwnerId,
                OwnerType = mapping.OwnerType,
                SpaceId = destinationSpaceId,
                Variables = mapping.Variables
            },
            mapping.RequiredInputs,
            mapping.Diagnostics);
    }

    private static VariableSetCommandMapping Map(
        OctopusResourceNode variableSetResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId,
        string name,
        string description)
    {
        ArgumentNullException.ThrowIfNull(variableSetResource);
        ArgumentNullException.ThrowIfNull(idMap);

        if (variableSetResource.Kind != OctopusResourceKind.VariableSet)
            throw new ArgumentException("Octopus variable mapper requires a current variable set resource.", nameof(variableSetResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var variableSet = variableSetResource.GetSource<OctopusVariableSetDto>()
            ?? throw new ArgumentException("Octopus variable set resource does not contain an OctopusVariableSetDto source.", nameof(variableSetResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var ownerType = MapOwnerType(variableSet, variableSetResource, diagnostics);
        var ownerId = MapOwnerId(variableSet, idMap, variableSetResource, diagnostics);
        var requiredInputs = new List<OctopusImportRequiredInputDto>();
        var variables = variableSet.Variables
            .Select((variable, index) => MapVariable(variableSet, variable, index, idMap, diagnostics, requiredInputs))
            .ToList();

        return new VariableSetCommandMapping(
            name,
            description,
            ownerId,
            ownerType,
            variables,
            requiredInputs,
            diagnostics);
    }

    private static VariableSetOwnerType MapOwnerType(
        OctopusVariableSetDto variableSet,
        OctopusResourceNode variableSetResource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (string.Equals(variableSet.OwnerType, "Project", StringComparison.OrdinalIgnoreCase))
            return VariableSetOwnerType.Project;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.UnsupportedVariableSetOwnerType,
            $"Octopus variable set owner type '{variableSet.OwnerType}' is not supported by the current Squid import variable mapper.",
            variableSetResource));

        return VariableSetOwnerType.Project;
    }

    private static int MapOwnerId(
        OctopusVariableSetDto variableSet,
        OctopusImportIdMap idMap,
        OctopusResourceNode variableSetResource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (idMap.TryGetDestinationId(variableSet.OwnerId, OctopusResourceKind.Project.ToString(), out var ownerId))
            return ownerId;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.MissingProjectMapping,
            $"Octopus variable set owner project '{variableSet.OwnerId}' has not been mapped to a destination project.",
            variableSetResource));

        return default;
    }

    private static VariableModel MapVariable(
        OctopusVariableSetDto variableSet,
        OctopusVariableDto variable,
        int index,
        OctopusImportIdMap idMap,
        List<OctopusImportDiagnosticDto> diagnostics,
        List<OctopusImportRequiredInputDto> requiredInputs)
    {
        var variableType = MapVariableType(variable, diagnostics);
        var isSensitive = OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable);

        if (isSensitive)
        {
            var variableSourceId = OctopusImportRequiredInputBuilder.BuildVariableSourceId(variableSet.Id, variable.Id, index);
            requiredInputs.Add(OctopusImportRequiredInputBuilder.ForSensitiveVariable(variableSourceId, variable));
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportVariableMappingDiagnosticCodes.SensitiveValueOmitted,
                $"Sensitive Octopus variable '{variable.Name}' was mapped without its source value and must be supplied manually after import.",
                OctopusResourceKind.Variable,
                variableSourceId,
                variable.Name));
        }

        if (!string.IsNullOrWhiteSpace(variable.Prompt?.DisplaySettings))
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportVariableMappingDiagnosticCodes.PromptDisplaySettingsOmitted,
                $"Octopus prompt display settings for variable '{variable.Name}' are not represented in Squid variable commands and were omitted.",
                OctopusResourceKind.Variable,
                variable.Id,
                variable.Name));
        }

        return new VariableModel
        {
            Name = variable.Name,
            Description = variable.Description,
            Value = isSensitive ? string.Empty : variable.Value,
            Type = variableType,
            IsSensitive = isSensitive,
            SortOrder = index,
            PromptLabel = variable.Prompt?.Label,
            PromptDescription = variable.Prompt?.Description,
            PromptRequired = variable.Prompt?.Required ?? false,
            Scopes = MapScopes(variable, idMap, diagnostics)
        };
    }

    private static VariableType MapVariableType(
        OctopusVariableDto variable,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (variable.IsSensitive || IsType(variable.Type, "Sensitive"))
            return VariableType.Password;

        var type = variable.Type?.Trim();

        if (string.IsNullOrWhiteSpace(type) || IsType(type, "String"))
            return VariableType.String;

        if (IsType(type, "Certificate"))
            return VariableType.Certificate;

        if (IsType(type, "Boolean"))
            return VariableType.Boolean;

        if (IsType(type, "Integer") || IsType(type, "Number"))
            return VariableType.Number;

        if (IsType(type, "MultiLineText"))
            return VariableType.MultiLineText;

        if (IsType(type, "SelectList") || IsType(type, "Select"))
            return VariableType.SelectList;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.UnsupportedVariableType,
            $"Octopus variable type '{variable.Type}' for variable '{variable.Name}' is not supported by the current Squid import variable mapper.",
            OctopusResourceKind.Variable,
            variable.Id,
            variable.Name));

        return VariableType.String;
    }

    private static List<VariableScopeModel> MapScopes(
        OctopusVariableDto variable,
        OctopusImportIdMap idMap,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var scopes = new List<VariableScopeModel>();

        foreach (var scope in variable.Scope ?? [])
        {
            var scopeType = MapScopeType(scope.Key);

            if (scopeType == null)
            {
                diagnostics.Add(Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    OctopusImportVariableMappingDiagnosticCodes.UnsupportedScopeType,
                    $"Octopus variable scope type '{scope.Key}' for variable '{variable.Name}' is not supported by Squid variable commands.",
                    OctopusResourceKind.Variable,
                    variable.Id,
                    variable.Name));
                continue;
            }

            foreach (var sourceScopeValue in scope.Value ?? [])
                AddScope(variable, scope.Key, scopeType.Value, sourceScopeValue, idMap, diagnostics, scopes);
        }

        return scopes;
    }

    private static void AddScope(
        OctopusVariableDto variable,
        string octopusScopeType,
        VariableScopeType scopeType,
        string sourceScopeValue,
        OctopusImportIdMap idMap,
        List<OctopusImportDiagnosticDto> diagnostics,
        List<VariableScopeModel> scopes)
    {
        if (string.IsNullOrWhiteSpace(sourceScopeValue))
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportVariableMappingDiagnosticCodes.EmptyScopeValue,
                $"Octopus variable '{variable.Name}' contains an empty '{octopusScopeType}' scope value that was omitted.",
                OctopusResourceKind.Variable,
                variable.Id,
                variable.Name));
            return;
        }

        if (scopeType == VariableScopeType.Role)
        {
            scopes.Add(new VariableScopeModel
            {
                ScopeType = scopeType,
                ScopeValue = sourceScopeValue
            });
            return;
        }

        var sourceKind = ScopeSourceKind(scopeType);
        if (sourceKind != null && idMap.TryGetDestinationId(sourceScopeValue, sourceKind.Value.ToString(), out var destinationId))
        {
            scopes.Add(new VariableScopeModel
            {
                ScopeType = scopeType,
                ScopeValue = destinationId.ToString(CultureInfo.InvariantCulture)
            });
            return;
        }

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.MissingScopeMapping,
            $"Octopus variable scope '{octopusScopeType}:{sourceScopeValue}' for variable '{variable.Name}' has not been mapped to a destination Squid scope value.",
            OctopusResourceKind.Variable,
            variable.Id,
            variable.Name));
    }

    private static VariableScopeType? MapScopeType(string octopusScopeType)
        => octopusScopeType?.Trim() switch
        {
            "Environment" => VariableScopeType.Environment,
            "Machine" => VariableScopeType.Machine,
            "Role" => VariableScopeType.Role,
            "Channel" => VariableScopeType.Channel,
            "Action" => VariableScopeType.Action,
            "Process" => VariableScopeType.Process,
            _ => null
        };

    private static OctopusResourceKind? ScopeSourceKind(VariableScopeType scopeType)
        => scopeType switch
        {
            VariableScopeType.Environment => OctopusResourceKind.Environment,
            VariableScopeType.Machine => OctopusResourceKind.Machine,
            VariableScopeType.Channel => OctopusResourceKind.Channel,
            VariableScopeType.Action => OctopusResourceKind.DeploymentAction,
            VariableScopeType.Process => OctopusResourceKind.DeploymentProcess,
            _ => null
        };

    private static bool IsType(string value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceNode resource)
        => Diagnostic(severity, code, message, resource.Kind, resource.SourceId, resource.Name);

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceKind resourceKind,
        string sourceId,
        string resourceName)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = resourceKind.ToString(),
            SourceId = sourceId,
            ResourceName = resourceName
        });

    private sealed record VariableSetCommandMapping(
        string Name,
        string Description,
        int OwnerId,
        VariableSetOwnerType OwnerType,
        List<VariableModel> Variables,
        IReadOnlyList<OctopusImportRequiredInputDto> RequiredInputs,
        IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics);
}

public sealed record OctopusImportVariableMappingResult(
    CreateVariableSetCommand CreateCommand,
    UpdateVariableSetCommand UpdateCommand,
    IReadOnlyList<OctopusImportRequiredInputDto> RequiredInputs,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
