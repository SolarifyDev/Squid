using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public class OctopusImportActionMapperRegistry : IOctopusImportActionMapperRegistry
{
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
                $"[{OctopusImportActionMappingDiagnosticCodes.MissingActionType}] Octopus action type is missing.");

        if (!_mappers.TryGetValue(actionType, out var mapper))
        {
            return Unsupported(
                action,
                OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType,
                $"[{OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType}] Octopus action type '{action.ActionType}' is not registered for import action mapping.");
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
        string message)
    {
        var diagnostics = new List<OctopusImportDiagnosticDto>
        {
            new()
            {
                Severity = OctopusImportCompatibilitySeverity.Blocker,
                Code = code,
                Message = message,
                ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
                SourceId = action.Id,
                ResourceName = action.Name
            }
        };

        return new OctopusImportActionMappingResult(null, diagnostics);
    }
}
