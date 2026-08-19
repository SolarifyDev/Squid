using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public class OctopusImportActionMapperRegistry : IOctopusImportActionMapperRegistry
{
    internal const string PlaceholderSourceIdProperty = "Squid.Import.Octopus.SourceAction.Id";
    internal const string PlaceholderSourceNameProperty = "Squid.Import.Octopus.SourceAction.Name";
    internal const string PlaceholderSourceSlugProperty = "Squid.Import.Octopus.SourceAction.Slug";
    internal const string PlaceholderSourceActionTypeProperty = "Squid.Import.Octopus.SourceAction.Type";
    internal const string RedactedValue = OctopusImportRedaction.RedactedValue;

    private readonly IReadOnlyDictionary<string, IOctopusImportActionMapper> _mappers;

    public OctopusImportActionMapperRegistry(IEnumerable<IOctopusImportActionMapper> mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);
        _mappers = BuildMapperIndex(mappers);
    }

    public IReadOnlyCollection<string> SupportedActionTypes => _mappers.Keys.ToArray();

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var actionType = action.ActionType?.Trim();
        if (string.IsNullOrWhiteSpace(actionType))
            return Unsupported(
                action,
                OctopusImportActionMappingDiagnosticCodes.MissingActionType,
                $"[{OctopusImportActionMappingDiagnosticCodes.MissingActionType}] Octopus action type is missing.",
                context.UnsupportedActionHandling);

        if (!_mappers.TryGetValue(actionType, out var mapper))
        {
            var redactedActionType = OctopusImportRedaction.RedactMetadataValue(PlaceholderSourceActionTypeProperty, action.ActionType);
            return Unsupported(
                action,
                OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType,
                $"[{OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType}] Octopus action type '{redactedActionType}' is not registered for import action mapping.",
                context.UnsupportedActionHandling);
        }

        var result = mapper.Map(action, context);

        if (result == null)
            throw new InvalidOperationException($"Mapper '{mapper.GetType().Name}' returned null for action type '{mapper.OctopusActionType}'.");

        return result;
    }

    private static IReadOnlyDictionary<string, IOctopusImportActionMapper> BuildMapperIndex(IEnumerable<IOctopusImportActionMapper> mappers)
    {
        var mapperList = mappers.ToList();
        var index = new Dictionary<string, IOctopusImportActionMapper>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapper in mapperList)
        {
            if (mapper == null)
                throw new ArgumentException("Import action mappers cannot contain null entries.", nameof(mappers));

            if (string.IsNullOrWhiteSpace(mapper.OctopusActionType))
            {
                throw new InvalidOperationException(
                    $"[{OctopusImportActionMappingDiagnosticCodes.InvalidActionMapperConfiguration}] Import action mapper '{mapper.GetType().Name}' must declare a non-empty Octopus action type.");
            }

            if (string.IsNullOrWhiteSpace(mapper.SquidActionType))
            {
                throw new InvalidOperationException(
                    $"[{OctopusImportActionMappingDiagnosticCodes.InvalidActionMapperConfiguration}] Import action mapper '{mapper.GetType().Name}' must declare a non-empty Squid action type.");
            }

            if (!index.TryAdd(mapper.OctopusActionType.Trim(), mapper))
            {
                throw new InvalidOperationException(
                    $"[{OctopusImportActionMappingDiagnosticCodes.DuplicateActionMapperRegistration}] Duplicate import action mapper registration detected for Octopus action type '{mapper.OctopusActionType}'.");
            }
        }

        return index;
    }

    private static OctopusImportActionMappingResult Unsupported(
        OctopusDeploymentActionDto action,
        string code,
        string message,
        OctopusImportUnsupportedActionHandling handling)
    {
        var redactedActionName = OctopusImportRedaction.RedactMetadataValue(PlaceholderSourceNameProperty, action.Name);
        var redactedSourceId = OctopusImportRedaction.RedactMetadataValue(PlaceholderSourceIdProperty, action.Id);
        var displayName = string.IsNullOrWhiteSpace(redactedActionName)
            ? "unsupported Octopus action"
            : redactedActionName;

        var diagnostics = new List<OctopusImportDiagnosticDto>
        {
            OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Warning,
                Code = code,
                Message = message,
                ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
                SourceId = redactedSourceId,
                ResourceName = redactedActionName
            })
        };

        if (handling == OctopusImportUnsupportedActionHandling.DisabledPlaceholder)
        {
            diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Warning,
                Code = OctopusImportActionMappingDiagnosticCodes.UnsupportedActionPlaceholderCreated,
                Message = $"[{OctopusImportActionMappingDiagnosticCodes.UnsupportedActionPlaceholderCreated}] Unsupported Octopus action '{displayName}' was imported as a disabled placeholder. Source action properties, packages, container settings, git dependencies, and extension data were omitted.",
                ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
                SourceId = redactedSourceId,
                ResourceName = redactedActionName
            }));

            return new OctopusImportActionMappingResult(CreateDisabledPlaceholder(action), diagnostics);
        }

        diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Warning,
            Code = OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSkipped,
            Message = $"[{OctopusImportActionMappingDiagnosticCodes.UnsupportedActionSkipped}] Unsupported Octopus action '{displayName}' was skipped during import. Source action properties, packages, container settings, git dependencies, and extension data were omitted.",
            ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
            SourceId = redactedSourceId,
            ResourceName = redactedActionName
        }));

        return new OctopusImportActionMappingResult(null, diagnostics);
    }

    private static CreateOrUpdateDeploymentActionModel CreateDisabledPlaceholder(OctopusDeploymentActionDto action)
        => new()
        {
            Name = string.IsNullOrWhiteSpace(OctopusImportRedaction.RedactMetadataValue(PlaceholderSourceNameProperty, action.Name))
                ? "Unsupported Octopus action"
                : OctopusImportRedaction.RedactMetadataValue(PlaceholderSourceNameProperty, action.Name),
            ActionType = SpecialVariables.ActionTypes.Script,
            IsDisabled = true,
            IsRequired = action.IsRequired,
            CanBeUsedForProjectVersioning = false,
            Properties =
            [
                Placeholder(PlaceholderSourceIdProperty, action.Id),
                Placeholder(PlaceholderSourceNameProperty, action.Name),
                Placeholder(PlaceholderSourceSlugProperty, action.Slug),
                Placeholder(PlaceholderSourceActionTypeProperty, action.ActionType)
            ]
        };

    private static ActionPropertyModel Placeholder(string propertyName, string value)
        => new()
        {
            PropertyName = propertyName,
            PropertyValue = OctopusImportRedaction.RedactMetadataValue(propertyName, value)
        };
}
