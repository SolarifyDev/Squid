using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportShadowedProjectVariable(OctopusResourceNode ProjectVariable, string ProjectName, string VariableName, string LibraryName);

/// <summary>
/// Finds project variables that Squid's runtime will NOT use. Squid resolves the project's variable set
/// first and its included library sets after it, and at equal scope rank the later variable wins, so a
/// library variable shadows a same-named project variable whenever both can apply with the same scope
/// types. Octopus resolves the opposite way. Squid's runtime precedence is deliberately unchanged; the
/// importer only reports the difference (pinned against the real resolver by an integration test).
/// </summary>
public static class OctopusImportVariablePrecedence
{
    public static IEnumerable<OctopusImportShadowedProjectVariable> FindProjectVariablesShadowedByLibraries(IReadOnlyCollection<OctopusResourceNode> resources)
    {
        foreach (var projectNode in resources.Where(r => r.Kind == OctopusResourceKind.Project && !r.IsHistorical))
        {
            var project = projectNode.GetSource<OctopusProjectDto>();

            if (project == null) continue;

            foreach (var shadow in FindForProject(project, resources))
                yield return shadow;
        }
    }

    private static IEnumerable<OctopusImportShadowedProjectVariable> FindForProject(OctopusProjectDto project, IReadOnlyCollection<OctopusResourceNode> resources)
    {
        var visibleSets = OctopusImportVariableOwnership.VariableSetsVisibleTo(project, resources);
        var librarySets = visibleSets.Where(s => OctopusResourceGraphBuilder.IsLibraryVariableSetOwner(s.GetSource<OctopusVariableSetDto>().OwnerType)).ToList();
        var projectSetIds = visibleSets.Except(librarySets).Select(s => s.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectVariables = resources.Where(r => r.Kind == OctopusResourceKind.Variable && !r.IsHistorical && r.ParentSourceId != null && projectSetIds.Contains(r.ParentSourceId)).ToList();

        foreach (var projectVariable in projectVariables)
        {
            var shadowingLibraries = librarySets.Where(set => (set.GetSource<OctopusVariableSetDto>().Variables ?? []).Any(v => BothApplyAtEqualRank(projectVariable.GetSource<OctopusVariableDto>(), v))).Select(set => LibraryName(set, resources)).ToList();

            if (shadowingLibraries.Count > 0)
                yield return new OctopusImportShadowedProjectVariable(projectVariable, project.Name, projectVariable.Name, string.Join("', '", shadowingLibraries));
        }
    }

    private static bool BothApplyAtEqualRank(OctopusVariableDto projectVariable, OctopusVariableDto libraryVariable)
    {
        if (projectVariable == null || !string.Equals(projectVariable.Name, libraryVariable.Name, StringComparison.OrdinalIgnoreCase)) return false;

        var projectScope = NormalizeScope(projectVariable.Scope);
        var libraryScope = NormalizeScope(libraryVariable.Scope);

        return projectScope.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(libraryScope.Keys) && projectScope.All(s => s.Value.Overlaps(libraryScope[s.Key]));
    }

    private static Dictionary<string, HashSet<string>> NormalizeScope(Dictionary<string, List<string>> scope)
        => (scope ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Key) && (s.Value ?? []).Any(v => !string.IsNullOrWhiteSpace(v)))
            .GroupBy(s => s.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(s => s.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static string LibraryName(OctopusResourceNode librarySet, IReadOnlyCollection<OctopusResourceNode> resources)
    {
        var libraryId = librarySet.GetSource<OctopusVariableSetDto>().OwnerId;

        return resources.FirstOrDefault(r => r.Kind == OctopusResourceKind.LibraryVariableSet && string.Equals(r.SourceId, libraryId, StringComparison.OrdinalIgnoreCase))?.Name ?? libraryId;
    }
}
