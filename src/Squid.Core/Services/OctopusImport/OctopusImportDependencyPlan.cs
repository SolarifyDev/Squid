using Squid.Message.Enums.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportDependencyPlan(
    IReadOnlyList<OctopusResourceNode> OrderedResources,
    IReadOnlyList<OctopusResourceDependency> AppliedDependencies,
    IReadOnlyList<OctopusResourceReference> OptionalReferences,
    IReadOnlyList<OctopusInputExtractionDiagnostic> Diagnostics,
    IReadOnlyList<OctopusResourceNode> OutOfScopeResources)
{
    public OctopusImportDependencyPlan(
        IReadOnlyList<OctopusResourceNode> orderedResources,
        IReadOnlyList<OctopusResourceDependency> appliedDependencies,
        IReadOnlyList<OctopusResourceReference> optionalReferences,
        IReadOnlyList<OctopusInputExtractionDiagnostic> diagnostics)
        : this(orderedResources, appliedDependencies, optionalReferences, diagnostics, [])
    {
    }

    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
