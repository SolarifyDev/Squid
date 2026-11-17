using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Whole-space scope (D4): which resources an imported project uses, directly or through what it uses. A project, its
/// own resources (channels, process, variables, releases, deployments) and everything they reference — followed through
/// every reference and every owner→child edge (a used library brings its variables and their scope targets, a used
/// lifecycle its phases and their environments) — are referenced. Anything else in the space is unreferenced: the
/// preview may skip it when Squid cannot import it, instead of blocking every project for something none of them uses.
/// <see cref="Everything"/> (the default on hand-built plans) treats every resource as referenced.
/// With a project selection the roots are the chosen projects and what they own instead, a lifecycle without phases (Octopus
/// deploys it to every environment) uses every environment, and a step property or variable value that is an account's id uses
/// that account; anything not reached is <see cref="IsOutsideSelection"/>.
/// </summary>
public sealed class OctopusImportReferenceScope
{
    private readonly IReadOnlySet<string> _referenced;
    private readonly IReadOnlyDictionary<string, HashSet<string>> _targets;
    private readonly IReadOnlyDictionary<string, HashSet<string>> _children;
    private readonly bool _selected;

    private OctopusImportReferenceScope(IReadOnlySet<string> referenced, IReadOnlyDictionary<string, HashSet<string>> targets, IReadOnlyDictionary<string, HashSet<string>> children, bool selected = false)
    {
        _referenced = referenced;
        _targets = targets;
        _children = children;
        _selected = selected;
    }

    public static OctopusImportReferenceScope Everything { get; } = new(null, new Dictionary<string, HashSet<string>>(), new Dictionary<string, HashSet<string>>());

    public bool IsReferenced(string sourceId) => _referenced == null || (sourceId != null && _referenced.Contains(sourceId));

    /// <summary>
    /// A project selection is present and this resource is neither a chosen project, something one owns, nor something they use. A
    /// shared resource Squid never imports (a machine, a certificate, a worker pool) is not: it is reported as today, whatever is chosen.
    /// </summary>
    public bool IsOutsideSelection(OctopusResourceNode resource)
        => _selected && resource != null && !IsReferenced(resource.SourceId) && !IsReportOnlyShared(resource);

    private static bool IsReportOnlyShared(OctopusResourceNode resource)
        => string.IsNullOrWhiteSpace(resource.OwnerProjectId) && (OctopusResourceMetadata.For(resource.Kind).IsUnsupported || OctopusResourceMetadata.For(resource.Kind).IsPreviewOutOfScope);

    /// <summary>
    /// What a resource uses: its own references and those of everything it owns (a lifecycle uses its phases'
    /// environments, a library its variables' scope targets), because confirmation writes them together.
    /// </summary>
    public IReadOnlyList<string> TargetsOf(string sourceId)
        => sourceId == null ? [] : Reach([sourceId], _children).SelectMany(id => _targets.GetValueOrDefault(id) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <param name="chosenProjectIds">The session's project selection; null is the whole space (D4, unchanged).</param>
    public static OctopusImportReferenceScope Build(OctopusResourceGraph graph, IReadOnlyCollection<string> chosenProjectIds = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var targets = ReferenceTargets(graph);
        var children = Children(graph);

        if (chosenProjectIds == null) return new OctopusImportReferenceScope(Reach(Roots(graph), Union(targets, children)), targets, children);

        var selection = new ProjectSelection(graph, chosenProjectIds);
        var edges = Union(Union(Union(targets, children), PhaselessLifecycleEnvironments(graph)), AccountsNamedByValue(graph));

        return new OctopusImportReferenceScope(Reach(Roots(graph), edges, selection.MayReach), targets, children, selected: true);
    }

    // A project and everything that belongs to it (configuration and history alike).
    private static IEnumerable<string> Roots(OctopusResourceGraph graph)
        => graph.Resources.Where(r => r.Kind == OctopusResourceKind.Project || !string.IsNullOrWhiteSpace(r.OwnerProjectId)).Select(r => r.SourceId);

    // Octopus deploys a lifecycle without phases to every environment of the space.
    private static Dictionary<string, HashSet<string>> PhaselessLifecycleEnvironments(OctopusResourceGraph graph)
    {
        var environments = graph.Resources.Where(r => r.Kind == OctopusResourceKind.Environment && !r.IsHistorical).Select(r => r.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return graph.Resources.Where(r => r.Kind == OctopusResourceKind.Lifecycle && r.GetSource<OctopusLifecycleDto>()?.Phases is { Count: 0 }).Select(r => r.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, _ => environments, StringComparer.OrdinalIgnoreCase);
    }

    // A step or a variable names an account by its id in a property or value, which is not a graph reference.
    private static Dictionary<string, HashSet<string>> AccountsNamedByValue(OctopusResourceGraph graph)
    {
        var accounts = graph.Resources.Where(r => r.Kind == OctopusResourceKind.Account).Select(r => r.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in graph.Resources)
            foreach (var accountId in ValuesOf(resource).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v) && accounts.Contains(v)))
                Add(named, resource.SourceId, accountId);

        return named;
    }

    private static IEnumerable<string> ValuesOf(OctopusResourceNode resource)
        => resource.Kind switch
        {
            OctopusResourceKind.DeploymentAction => resource.GetSource<OctopusDeploymentActionDto>()?.Properties?.Values.AsEnumerable() ?? [],
            OctopusResourceKind.Variable => [resource.GetSource<OctopusVariableDto>()?.Value],
            _ => []
        };

    private static Dictionary<string, HashSet<string>> ReferenceTargets(OctopusResourceGraph graph)
    {
        var targets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in graph.References.Where(r => !string.IsNullOrWhiteSpace(r.ToSourceId)))
            Add(targets, reference.FromSourceId, reference.ToSourceId);

        foreach (var (releaseId, feedId) in ReleasePackageFeeds(graph))
            Add(targets, releaseId, feedId);

        return targets;
    }

    // A release pulls its packages from the feeds of its process snapshot, which are not graph references.
    private static IEnumerable<(string ReleaseId, string FeedId)> ReleasePackageFeeds(OctopusResourceGraph graph)
    {
        var feeds = new OctopusImportReleasePackageFeedLookup(graph, graph.Resources.Where(r => !r.IsHistorical));

        foreach (var node in graph.Resources.Where(r => r.Kind == OctopusResourceKind.Release))
        {
            var release = node.GetSource<OctopusReleaseDto>();

            foreach (var package in release?.SelectedPackages ?? [])
            {
                var feedId = feeds.GetSourceFeedId(release, package);

                if (!string.IsNullOrWhiteSpace(feedId)) yield return (node.SourceId, feedId);
            }
        }
    }

    private static Dictionary<string, HashSet<string>> Children(OctopusResourceGraph graph)
    {
        var children = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in graph.Resources.Where(r => !string.IsNullOrWhiteSpace(r.ParentSourceId)))
            Add(children, child.ParentSourceId, child.SourceId);

        return children;
    }

    private static Dictionary<string, HashSet<string>> Union(Dictionary<string, HashSet<string>> left, Dictionary<string, HashSet<string>> right)
    {
        var union = left.ToDictionary(t => t.Key, t => new HashSet<string>(t.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var (from, to) in right.SelectMany(e => e.Value.Select(to => (e.Key, to))))
            Add(union, from, to);

        return union;
    }

    // With a selection, a project that is not chosen, and what such a project owns, is never entered (not even as a root).
    private static HashSet<string> Reach(IEnumerable<string> roots, IReadOnlyDictionary<string, HashSet<string>> edges, Func<string, bool> mayEnter = null)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);

        while (pending.Count > 0)
        {
            var next = pending.Pop();

            if (mayEnter?.Invoke(next) == false || !reached.Add(next) || !edges.TryGetValue(next, out var targets)) continue;

            foreach (var target in targets) pending.Push(target);
        }

        return reached;
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var set)) map[from] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        set.Add(to);
    }

    // Ids compare after the source-map normalisation, so an export's suffixed project id matches its bare id and the reverse.
    private sealed class ProjectSelection(OctopusResourceGraph graph, IReadOnlyCollection<string> chosenProjectIds)
    {
        private readonly HashSet<string> _chosen = chosenProjectIds.Select(OctopusImportPriorMappings.Normalize).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, OctopusResourceNode> _nodes = graph.Resources.GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        /// <summary>What the selection may reach: anything no project owns, or a chosen project and what it owns.</summary>
        public bool MayReach(string sourceId)
        {
            if (!_nodes.TryGetValue(sourceId, out var node)) return true;

            var projectId = node.Kind == OctopusResourceKind.Project ? node.SourceId : node.OwnerProjectId;

            return string.IsNullOrWhiteSpace(projectId) || _chosen.Contains(OctopusImportPriorMappings.Normalize(projectId));
        }
    }
}
