using System.Globalization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Core.Services.OctopusImport.Secrets;
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
    private readonly IOctopusExportSecretsAccessor _secretsAccessor;

    public OctopusImportVariableMapper(IOctopusExportSecretsAccessor secretsAccessor = null)
    {
        _secretsAccessor = secretsAccessor;
    }

    private OctopusExportSecrets Secrets => _secretsAccessor?.Secrets ?? OctopusExportSecrets.None;

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

    private VariableSetCommandMapping Map(
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
        var ownerId = MapOwnerId(variableSet, ownerType, idMap, variableSetResource, diagnostics);
        var requiredInputs = new List<OctopusImportRequiredInputDto>();
        var secrets = Secrets;
        var variables = variableSet.Variables
            .Select((variable, index) => MapVariable(new VariableMappingContext(variableSet, idMap, secrets, diagnostics, requiredInputs), variable, index))
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

        if (OctopusResourceGraphBuilder.IsLibraryVariableSetOwner(variableSet.OwnerType))
            return VariableSetOwnerType.LibraryVariableSet;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.UnsupportedVariableSetOwnerType,
            $"Octopus variable set owner type '{variableSet.OwnerType}' is not supported by the current Squid import variable mapper.",
            variableSetResource));

        return VariableSetOwnerType.Project;
    }

    private static int MapOwnerId(
        OctopusVariableSetDto variableSet,
        VariableSetOwnerType ownerType,
        OctopusImportIdMap idMap,
        OctopusResourceNode variableSetResource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (ownerType == VariableSetOwnerType.LibraryVariableSet)
            return MapLibraryVariableSetOwnerId(variableSet, idMap);

        if (idMap.TryGetDestinationId(variableSet.OwnerId, OctopusResourceKind.Project.ToString(), out var ownerId))
            return ownerId;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.MissingProjectMapping,
            $"Octopus variable set owner project '{variableSet.OwnerId}' has not been mapped to a destination project.",
            variableSetResource));

        return default;
    }

    // A library variable set's owner is created by VariableService itself (it inserts the
    // LibraryVariableSet row and back-fills OwnerId), so an unmapped owner on CREATE is expected
    // and 0 is the correct placeholder; on UPDATE the confirmation flow has already mapped it.
    private static int MapLibraryVariableSetOwnerId(OctopusVariableSetDto variableSet, OctopusImportIdMap idMap)
        => idMap.TryGetDestinationId(variableSet.OwnerId, OctopusResourceKind.LibraryVariableSet.ToString(), out var ownerId) ? ownerId : default;

    private static VariableModel MapVariable(VariableMappingContext context, OctopusVariableDto variable, int index)
    {
        var diagnostics = context.Diagnostics;
        var variableType = MapVariableType(variable, diagnostics);
        var isSensitive = OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable);
        var value = OctopusSystemVariables.RewriteReferences(isSensitive ? ResolveSensitiveValue(context, variable, index) : variable.Value);

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
            Value = value,
            Type = variableType,
            IsSensitive = isSensitive,
            SortOrder = index,
            PromptLabel = variable.Prompt?.Label,
            PromptDescription = variable.Prompt?.Description,
            PromptRequired = variable.Prompt?.Required ?? false,
            Scopes = MapScopes(variable, context.IdMap, diagnostics)
        };
    }

    private static string ResolveSensitiveValue(VariableMappingContext context, OctopusVariableDto variable, int index)
    {
        if (OctopusImportSensitiveValues.TryResolve(variable, context.Secrets, out var plaintext)) return plaintext;

        var variableSourceId = OctopusImportRequiredInputBuilder.BuildVariableSourceId(context.VariableSet.Id, variable.Id, index);

        context.RequiredInputs.Add(OctopusImportRequiredInputBuilder.ForSensitiveVariable(variableSourceId, variable));
        context.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportVariableMappingDiagnosticCodes.SensitiveValueOmitted,
            $"Sensitive Octopus variable '{variable.Name}' cannot be imported without its source value. Confirmation is blocked while this required input is unresolved.",
            OctopusResourceKind.Variable,
            variableSourceId,
            variable.Name));

        return string.Empty;
    }

    private static VariableType MapVariableType(
        OctopusVariableDto variable,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (OctopusImportRequiredInputBuilder.IsSensitiveVariable(variable))
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

    private static List<VariableScopeModel> MapScopes(OctopusVariableDto variable, OctopusImportIdMap idMap, List<OctopusImportDiagnosticDto> diagnostics)
    {
        var scopes = new List<VariableScopeModel>();

        foreach (var resolution in OctopusImportVariableScopeRule.ResolveAll(variable.Scope, (sourceId, kind) => idMap.TryGetDestinationId(sourceId, kind.ToString(), out _)))
            AddScope(variable, resolution, idMap, diagnostics, scopes);

        return scopes;
    }

    private static void AddScope(OctopusVariableDto variable, OctopusImportScopeResolution resolution, OctopusImportIdMap idMap, List<OctopusImportDiagnosticDto> diagnostics, List<VariableScopeModel> scopes)
    {
        switch (resolution.Outcome)
        {
            case OctopusImportScopeOutcome.PassThrough:
                scopes.Add(new VariableScopeModel { ScopeType = resolution.ScopeType!.Value, ScopeValue = resolution.ScopeValue });
                return;
            case OctopusImportScopeOutcome.Mapped:
                idMap.TryGetDestinationId(resolution.ScopeValue, resolution.SourceKind!.Value.ToString(), out var destinationId);
                scopes.Add(new VariableScopeModel { ScopeType = resolution.ScopeType!.Value, ScopeValue = destinationId.ToString(CultureInfo.InvariantCulture) });
                return;
            case OctopusImportScopeOutcome.EmptyValue:
                diagnostics.Add(VariableDiagnostic(variable, OctopusImportCompatibilitySeverity.Warning, OctopusImportVariableMappingDiagnosticCodes.EmptyScopeValue, $"Octopus variable '{variable.Name}' contains an empty '{resolution.ScopeKey}' scope value that was omitted."));
                return;
            case OctopusImportScopeOutcome.UnsupportedType:
                diagnostics.Add(VariableDiagnostic(variable, OctopusImportCompatibilitySeverity.Blocker, OctopusImportVariableMappingDiagnosticCodes.UnsupportedScopeType, $"Octopus variable scope type '{resolution.ScopeKey}' for variable '{variable.Name}' is not supported by Squid variable commands."));
                return;
            default:
                diagnostics.Add(VariableDiagnostic(variable, OctopusImportCompatibilitySeverity.Blocker, OctopusImportVariableMappingDiagnosticCodes.MissingScopeMapping, $"Octopus variable scope '{resolution.ScopeKey}:{resolution.ScopeValue}' for variable '{variable.Name}' has not been mapped to a destination Squid scope value."));
                return;
        }
    }

    private static OctopusImportDiagnosticDto VariableDiagnostic(OctopusVariableDto variable, OctopusImportCompatibilitySeverity severity, string code, string message)
        => Diagnostic(severity, code, message, OctopusResourceKind.Variable, variable.Id, variable.Name);

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

    private sealed record VariableMappingContext(
        OctopusVariableSetDto VariableSet,
        OctopusImportIdMap IdMap,
        OctopusExportSecrets Secrets,
        List<OctopusImportDiagnosticDto> Diagnostics,
        List<OctopusImportRequiredInputDto> RequiredInputs);

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
