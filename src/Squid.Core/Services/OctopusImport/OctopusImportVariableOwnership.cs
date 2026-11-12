using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Single source of truth for who owns an imported variable and whether THIS import writes it.
/// A library variable set that resolves to <see cref="OctopusImportPreviewAction.ReuseExisting"/> or
/// <see cref="OctopusImportPreviewAction.AlreadyImported"/> is linked as-is and its variables are never
/// rewritten, and a resource that is itself already imported is never written again, so nothing they
/// hold can block the import.
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
        if (resource != null && previewActionOf(resource.SourceId) == OctopusImportPreviewAction.AlreadyImported) return false;

        var libraryId = FindOwningLibraryId(resource, findResource);

        return string.IsNullOrWhiteSpace(libraryId) || !IsKeptAsIs(previewActionOf(libraryId));
    }

    // A library that is reused or was imported earlier is linked as-is: its variables are never rewritten.
    private static bool IsKeptAsIs(OctopusImportPreviewAction? libraryAction)
        => libraryAction is OctopusImportPreviewAction.ReuseExisting or OctopusImportPreviewAction.AlreadyImported;

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
