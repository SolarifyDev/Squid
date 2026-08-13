using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportDependencyPlanner : IScopedDependency
{
    OctopusImportDependencyPlan BuildCurrentConfigurationPlan(OctopusResourceGraph graph);
}

public class OctopusImportDependencyPlanner : IOctopusImportDependencyPlanner
{
    public OctopusImportDependencyPlan BuildCurrentConfigurationPlan(OctopusResourceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var diagnostics = new List<OctopusInputExtractionDiagnostic>(graph.Diagnostics);
        var outOfScopeResources = graph.Resources
            .Where(IsOutOfScopeReportResource)
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => Rank(r.Kind))
            .ThenBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resources = graph.Resources
            .Where(r => !r.IsHistorical && IsOrderable(r.Kind))
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(r => r.SourceId, StringComparer.OrdinalIgnoreCase);

        var orderingEdges = BuildOrderingEdges(graph, resources);
        var orderedResources = OrderResources(resources, orderingEdges, diagnostics);
        var appliedDependencies = orderingEdges
            .Select(e => new OctopusResourceDependency(e.SourceId, e.DependsOnSourceId, e.ReferenceKind, e.DependsOnKind))
            .Distinct()
            .ToList();
        var optionalReferences = graph.References
            .Where(r => !r.IsRequired && resources.ContainsKey(r.FromSourceId))
            .OrderBy(r => Rank(r.FromKind))
            .ThenBy(r => r.FromSourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ReferenceKind)
            .ThenBy(r => r.ToSourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new OctopusImportDependencyPlan(orderedResources, appliedDependencies, optionalReferences, diagnostics, outOfScopeResources);
    }

    private static IReadOnlyList<OrderingEdge> BuildOrderingEdges(OctopusResourceGraph graph, Dictionary<string, OctopusResourceNode> resources)
    {
        var edges = new List<OrderingEdge>();

        foreach (var dependency in graph.Dependencies)
        {
            if (!resources.ContainsKey(dependency.SourceId) || !resources.ContainsKey(dependency.DependsOnSourceId))
                continue;

            edges.Add(new OrderingEdge(dependency.SourceId, dependency.DependsOnSourceId, dependency.ReferenceKind, dependency.DependsOnKind));
        }

        foreach (var resource in resources.Values)
        {
            if (string.IsNullOrWhiteSpace(resource.ParentSourceId) || !resources.TryGetValue(resource.ParentSourceId, out var parent))
                continue;

            edges.Add(new OrderingEdge(resource.SourceId, parent.SourceId, OctopusResourceReferenceKind.Parent, parent.Kind));
        }

        return edges.Distinct().ToList();
    }

    private static IReadOnlyList<OctopusResourceNode> OrderResources(
        Dictionary<string, OctopusResourceNode> resources,
        IReadOnlyList<OrderingEdge> orderingEdges,
        List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        var incoming = resources.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var outgoing = resources.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in orderingEdges)
        {
            incoming[edge.SourceId].Add(edge.DependsOnSourceId);
            outgoing[edge.DependsOnSourceId].Add(edge.SourceId);
        }

        var ordered = new List<OctopusResourceNode>();
        var ready = resources.Values
            .Where(r => incoming[r.SourceId].Count == 0)
            .ToList();

        while (ready.Count > 0)
        {
            ready.Sort(CompareResources);
            var next = ready[0];
            ready.RemoveAt(0);
            ordered.Add(next);

            foreach (var dependentId in outgoing[next.SourceId].OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList())
            {
                incoming[dependentId].Remove(next.SourceId);

                if (incoming[dependentId].Count == 0)
                    ready.Add(resources[dependentId]);
            }
        }

        if (ordered.Count == resources.Count)
            return ordered;

        var remaining = resources.Values
            .Where(r => incoming[r.SourceId].Count > 0)
            .OrderBy(r => Rank(r.Kind))
            .ThenBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        diagnostics.Add(new OctopusInputExtractionDiagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusInputExtractionDiagnosticCodes.DependencyCycle,
            "Octopus import current-configuration dependency graph contains a cycle.",
            SourceId: remaining.FirstOrDefault()?.SourceId,
            DocumentKind: remaining.FirstOrDefault()?.DocumentKind));

        ordered.AddRange(remaining);
        return ordered;
    }

    private static int CompareResources(OctopusResourceNode left, OctopusResourceNode right)
    {
        var rankComparison = Rank(left.Kind).CompareTo(Rank(right.Kind));
        if (rankComparison != 0)
            return rankComparison;

        return string.Compare(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrderable(OctopusResourceKind kind)
        => kind is not (OctopusResourceKind.Unknown or OctopusResourceKind.ActionTemplate);

    private static bool IsOutOfScopeReportResource(OctopusResourceNode resource)
        => resource.Kind is OctopusResourceKind.Release
            or OctopusResourceKind.Deployment
            or OctopusResourceKind.ServerTask
            or OctopusResourceKind.DeploymentProcessSnapshot
            or OctopusResourceKind.VariableSetSnapshot;

    private static int Rank(OctopusResourceKind kind)
    {
        return kind switch
        {
            OctopusResourceKind.ProjectGroup => 10,
            OctopusResourceKind.Environment => 20,
            OctopusResourceKind.Lifecycle => 30,
            OctopusResourceKind.LifecyclePhase => 40,
            OctopusResourceKind.Feed => 50,
            OctopusResourceKind.Team => 60,
            OctopusResourceKind.Machine => 70,
            OctopusResourceKind.Account => 80,
            OctopusResourceKind.Certificate => 90,
            OctopusResourceKind.Project => 100,
            OctopusResourceKind.Channel => 110,
            OctopusResourceKind.DeploymentSettings => 120,
            OctopusResourceKind.VariableSet => 130,
            OctopusResourceKind.Variable => 140,
            OctopusResourceKind.DeploymentProcess => 150,
            OctopusResourceKind.DeploymentStep => 160,
            OctopusResourceKind.DeploymentAction => 170,
            _ => 1000
        };
    }

    private sealed record OrderingEdge(
        string SourceId,
        string DependsOnSourceId,
        OctopusResourceReferenceKind ReferenceKind,
        OctopusResourceKind? DependsOnKind);
}
