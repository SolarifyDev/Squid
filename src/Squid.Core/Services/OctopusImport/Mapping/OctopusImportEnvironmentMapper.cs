using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportEnvironmentMapper : IScopedDependency
{
    OctopusImportEnvironmentMappingResult MapToCreateModel(
        OctopusResourceNode environmentResource,
        int destinationSpaceId);
}

public class OctopusImportEnvironmentMapper : IOctopusImportEnvironmentMapper
{
    public OctopusImportEnvironmentMappingResult MapToCreateModel(
        OctopusResourceNode environmentResource,
        int destinationSpaceId)
    {
        ArgumentNullException.ThrowIfNull(environmentResource);

        if (environmentResource.Kind != OctopusResourceKind.Environment)
            throw new ArgumentException("Octopus environment mapper requires an environment resource.", nameof(environmentResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var environment = environmentResource.GetSource<OctopusEnvironmentDto>()
            ?? throw new ArgumentException("Octopus environment resource does not contain an OctopusEnvironmentDto source.", nameof(environmentResource));

        return new OctopusImportEnvironmentMappingResult(
            new CreateEnvironmentCommand
            {
                SpaceId = destinationSpaceId,
                Slug = environment.Slug,
                Name = environment.Name,
                Description = environment.Description,
                SortOrder = environment.SortOrder,
                UseGuidedFailure = environment.UseGuidedFailure ?? false,
                AllowDynamicInfrastructure = environment.AllowDynamicInfrastructure ?? false
            },
            []);
    }
}

public sealed record OctopusImportEnvironmentMappingResult(
    CreateEnvironmentCommand Environment,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
