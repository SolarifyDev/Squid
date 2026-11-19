using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusImportActionMappingContext
{
    public OctopusImportActionMappingContext(
        OctopusImportIdMap idMap,
        int destinationSpaceId,
        OctopusImportUnsupportedActionHandling unsupportedActionHandling = OctopusImportUnsupportedActionHandling.Skip,
        IReadOnlyDictionary<string, OctopusActionTemplateDto> actionTemplates = null)
    {
        ActionTemplates = actionTemplates ?? new Dictionary<string, OctopusActionTemplateDto>(StringComparer.OrdinalIgnoreCase);

        IdMap = idMap ?? throw new ArgumentNullException(nameof(idMap));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        DestinationSpaceId = destinationSpaceId;
        UnsupportedActionHandling = unsupportedActionHandling;
    }

    public OctopusImportIdMap IdMap { get; }

    public int DestinationSpaceId { get; }

    public OctopusImportUnsupportedActionHandling UnsupportedActionHandling { get; }

    /// <summary>Step templates from the export, keyed by Octopus template id (case-insensitive).</summary>
    public IReadOnlyDictionary<string, OctopusActionTemplateDto> ActionTemplates { get; }

    public OctopusActionTemplateDto FindActionTemplate(string templateId)
        => !string.IsNullOrWhiteSpace(templateId) && ActionTemplates.TryGetValue(templateId.Trim(), out var template) ? template : null;
}

public enum OctopusImportUnsupportedActionHandling
{
    Skip = 0,
    DisabledPlaceholder = 1
}

public interface IOctopusImportActionMapper : IScopedDependency
{
    string OctopusActionType { get; }

    string SquidActionType { get; }

    OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context);
}

public interface IOctopusImportActionMapperRegistry : IScopedDependency
{
    IReadOnlyCollection<string> SupportedActionTypes { get; }

    OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context);
}

public sealed record OctopusImportActionMappingResult(
    CreateOrUpdateDeploymentActionModel Action,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
