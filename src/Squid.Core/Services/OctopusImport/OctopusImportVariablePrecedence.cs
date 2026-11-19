using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <param name="LibraryReused">The library is linked as-is, so the DESTINATION library's variables decide at runtime, not the exported ones.</param>
public sealed record OctopusImportShadowedProjectVariable(OctopusResourceNode ProjectVariable, string ProjectName, string VariableName, string LibraryName, bool LibraryReused);

/// <summary>
/// Finds project variables that Squid's runtime will NOT use. Squid resolves the project's variable set
/// first and its included library sets after it, and at equal scope rank the later variable wins, so a
/// library variable shadows a same-named project variable whenever both can apply with the same scope
/// types. Octopus resolves the opposite way. Squid's runtime precedence is deliberately unchanged; the
/// importer only reports the difference (pinned against the real resolver by an integration test).
/// Whether a library is written or reused comes from the ownership SSOT the caller passes in.
/// </summary>
public static class OctopusImportVariablePrecedence
{
    public static IEnumerable<OctopusImportShadowedProjectVariable> FindProjectVariablesShadowedByLibraries(IReadOnlyCollection<OctopusResourceNode> resources, Func<OctopusResourceNode, bool> isWrittenByImport)
    {
        foreach (var projectNode in resources.Where(r => r.Kind == OctopusResourceKind.Project && !r.IsHistorical))
        {
            var project = projectNode.GetSource<OctopusProjectDto>();

            if (project == null) continue;

            foreach (var shadow in FindForProject(project, resources, isWrittenByImport))
                yield return shadow;
        }
    }

    private static IEnumerable<OctopusImportShadowedProjectVariable> FindForProject(OctopusProjectDto project, IReadOnlyCollection<OctopusResourceNode> resources, Func<OctopusResourceNode, bool> isWrittenByImport)
    {
        var visibleSets = OctopusImportVariableOwnership.VariableSetsVisibleTo(project, resources);
        var librarySets = visibleSets.Where(s => OctopusResourceGraphBuilder.IsLibraryVariableSetOwner(s.GetSource<OctopusVariableSetDto>().OwnerType)).ToList();
        var projectSetIds = visibleSets.Except(librarySets).Select(s => s.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectVariables = resources.Where(r => r.Kind == OctopusResourceKind.Variable && !r.IsHistorical && r.ParentSourceId != null && projectSetIds.Contains(r.ParentSourceId)).ToList();

        foreach (var projectVariable in projectVariables)
        {
            foreach (var librarySet in librarySets.Where(set => DefinesShadowingVariable(set, projectVariable)))
                yield return new OctopusImportShadowedProjectVariable(projectVariable, project.Name, projectVariable.Name, LibraryName(librarySet, resources), !isWrittenByImport(librarySet));
        }
    }

    private static bool DefinesShadowingVariable(OctopusResourceNode librarySet, OctopusResourceNode projectVariable)
        => (librarySet.GetSource<OctopusVariableSetDto>().Variables ?? []).Any(v => BothApplyAtEqualRank(projectVariable.GetSource<OctopusVariableDto>(), v));

    private static bool BothApplyAtEqualRank(OctopusVariableDto projectVariable, OctopusVariableDto libraryVariable)
    {
        if (projectVariable == null || !string.Equals(projectVariable.Name, libraryVariable.Name, StringComparison.OrdinalIgnoreCase)) return false;

        var projectScope = NormalizeScope(projectVariable.Scope);
        var libraryScope = NormalizeScope(libraryVariable.Scope);

        return projectScope.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(libraryScope.Keys) && projectScope.All(s => CanBothMatch(s.Key, s.Value, libraryScope[s.Key]));
    }

    // A deployment has ONE environment, machine, channel, action and process, so those scopes co-apply only
    // when their value sets overlap. A target holds MANY roles (and gains more after the import), and the
    // runtime matches a Role scope on ANY held role, so two role scopes can always meet on one target.
    private static bool CanBothMatch(string scopeKey, IReadOnlySet<string> projectValues, IReadOnlySet<string> libraryValues)
        => string.Equals(scopeKey, "Role", StringComparison.OrdinalIgnoreCase) || projectValues.Overlaps(libraryValues);

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
