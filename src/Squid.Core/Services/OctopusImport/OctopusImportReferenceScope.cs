using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Whole-space scope (D4): which resources an imported project uses, directly or through what it uses. A project, its
/// own resources (channels, process, variables, releases, deployments) and everything they reference — followed through
/// every reference and every owner→child edge (a used library brings its variables and their scope targets, a used
/// lifecycle its phases and their environments) — are referenced. Anything else in the space is unreferenced: the
/// preview may skip it when Squid cannot import it, instead of blocking every project for something none of them uses.
/// <see cref="Everything"/> (the default on hand-built plans) treats every resource as referenced.
/// </summary>
public sealed class OctopusImportReferenceScope
{
    private readonly IReadOnlySet<string> _referenced;
    private readonly IReadOnlyDictionary<string, HashSet<string>> _targets;
    private readonly IReadOnlyDictionary<string, HashSet<string>> _children;

    private OctopusImportReferenceScope(IReadOnlySet<string> referenced, IReadOnlyDictionary<string, HashSet<string>> targets, IReadOnlyDictionary<string, HashSet<string>> children)
    {
        _referenced = referenced;
        _targets = targets;
        _children = children;
    }

    public static OctopusImportReferenceScope Everything { get; } = new(null, new Dictionary<string, HashSet<string>>(), new Dictionary<string, HashSet<string>>());

    public bool IsReferenced(string sourceId) => _referenced == null || (sourceId != null && _referenced.Contains(sourceId));

    /// <summary>
    /// What a resource uses: its own references and those of everything it owns (a lifecycle uses its phases'
    /// environments, a library its variables' scope targets), because confirmation writes them together.
    /// </summary>
    public IReadOnlyList<string> TargetsOf(string sourceId)
        => sourceId == null ? [] : Reach([sourceId], _children).SelectMany(id => _targets.GetValueOrDefault(id) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static OctopusImportReferenceScope Build(OctopusResourceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var targets = ReferenceTargets(graph);
        var children = Children(graph);

        return new OctopusImportReferenceScope(Reach(Roots(graph), Union(targets, children)), targets, children);
    }

    // A project and everything that belongs to it (configuration and history alike).
    private static IEnumerable<string> Roots(OctopusResourceGraph graph)
        => graph.Resources.Where(r => r.Kind == OctopusResourceKind.Project || !string.IsNullOrWhiteSpace(r.OwnerProjectId)).Select(r => r.SourceId);

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
        var current = graph.Resources.Where(r => !r.IsHistorical).ToList();

        foreach (var node in graph.Resources.Where(r => r.Kind == OctopusResourceKind.Release))
        {
            var release = node.GetSource<OctopusReleaseDto>();

            foreach (var package in release?.SelectedPackages ?? [])
            {
                var feedId = OctopusImportReleasePackageFeedResolver.GetSourceFeedId(graph, current, release, package);

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

    private static HashSet<string> Reach(IEnumerable<string> roots, IReadOnlyDictionary<string, HashSet<string>> edges)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);

        while (pending.Count > 0)
        {
            var next = pending.Pop();

            if (!reached.Add(next) || !edges.TryGetValue(next, out var targets)) continue;

            foreach (var target in targets) pending.Push(target);
        }

        return reached;
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string from, string to)
    {
        if (!map.TryGetValue(from, out var set)) map[from] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        set.Add(to);
    }
}
