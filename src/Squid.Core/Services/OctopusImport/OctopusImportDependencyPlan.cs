using Squid.Message.Enums.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportDependencyPlan(
    IReadOnlyList<OctopusResourceNode> OrderedResources,
    IReadOnlyList<OctopusResourceDependency> AppliedDependencies,
    IReadOnlyList<OctopusResourceReference> OptionalReferences,
    IReadOnlyList<OctopusInputExtractionDiagnostic> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
