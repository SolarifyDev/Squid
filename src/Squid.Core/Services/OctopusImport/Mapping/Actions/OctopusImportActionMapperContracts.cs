using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusImportActionMappingContext
{
    public OctopusImportActionMappingContext(OctopusImportIdMap idMap, int destinationSpaceId)
    {
        IdMap = idMap ?? throw new ArgumentNullException(nameof(idMap));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        DestinationSpaceId = destinationSpaceId;
    }

    public OctopusImportIdMap IdMap { get; }

    public int DestinationSpaceId { get; }
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
