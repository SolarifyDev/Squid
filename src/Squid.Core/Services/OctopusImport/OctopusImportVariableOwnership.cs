using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Single source of truth for who owns an imported variable and whether THIS import writes it.
/// A library variable set that resolves to <see cref="OctopusImportPreviewAction.ReuseExisting"/> is
/// linked as-is and its variables are never rewritten, so nothing it holds can block the import.
/// The preview planner, preview validator and confirmation orchestrator all ask this one rule.
/// </summary>
public static class OctopusImportVariableOwnership
{
    public static string FindOwningLibraryId(OctopusResourceNode resource, Func<string, OctopusResourceNode> findResource)
    {
        var variableSet = resource?.Kind == OctopusResourceKind.Variable ? FindOrNull(findResource, resource.ParentSourceId) : resource;

        if (variableSet?.Kind != OctopusResourceKind.VariableSet) return null;

        var source = variableSet.GetSource<OctopusVariableSetDto>();

        return OctopusResourceGraphBuilder.IsLibraryVariableSetOwner(source?.OwnerType) ? source.OwnerId : null;
    }

    public static bool IsWrittenByImport(OctopusResourceNode resource, Func<string, OctopusResourceNode> findResource, Func<string, OctopusImportPreviewAction?> previewActionOf)
    {
        var libraryId = FindOwningLibraryId(resource, findResource);

        return string.IsNullOrWhiteSpace(libraryId) || previewActionOf(libraryId) != OctopusImportPreviewAction.ReuseExisting;
    }

    /// <summary>
    /// The variable sets a project's deployments resolve at runtime: its own set plus the sets of the
    /// library variable sets it includes — never another project's or a non-included library's.
    /// </summary>
    public static IReadOnlyList<OctopusResourceNode> VariableSetsVisibleTo(OctopusProjectDto project, IEnumerable<OctopusResourceNode> resources)
    {
        var includedLibraryIds = (project.IncludedLibraryVariableSetIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return resources.Where(r => r.Kind == OctopusResourceKind.VariableSet && !r.IsHistorical && IsVisibleTo(r.GetSource<OctopusVariableSetDto>(), project.Id, includedLibraryIds)).ToList();
    }

    private static bool IsVisibleTo(OctopusVariableSetDto variableSet, string projectId, IReadOnlySet<string> includedLibraryIds)
    {
        if (variableSet?.OwnerId == null) return false;

        return OctopusResourceGraphBuilder.IsLibraryVariableSetOwner(variableSet.OwnerType)
            ? includedLibraryIds.Contains(variableSet.OwnerId)
            : string.Equals(variableSet.OwnerId, projectId, StringComparison.OrdinalIgnoreCase);
    }

    private static OctopusResourceNode FindOrNull(Func<string, OctopusResourceNode> findResource, string sourceId)
        => string.IsNullOrWhiteSpace(sourceId) ? null : findResource(sourceId);
}
