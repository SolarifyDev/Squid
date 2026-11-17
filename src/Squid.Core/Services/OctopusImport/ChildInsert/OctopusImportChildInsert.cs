using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.ChildInsert;

/// <summary>
/// One selected child to insert into the Squid owner an earlier import created, at <paramref name="Order"/> among the owner's rows.
/// Lives in Core, not Messages: it carries the graph and the id map. <paramref name="CurrentNode"/> is the confirmation's index of the
/// graph's current nodes, so inserting many children never passes over the whole graph (history included) once per child.
/// </summary>
public sealed record OctopusImportChildInsert(OctopusResourceNode Resource, int OwnerDestinationId, int Order, OctopusImportIdMap IdMap, int SpaceId, OctopusResourceGraph Graph, Func<string, OctopusResourceKind, OctopusResourceNode> CurrentNode = null)
{
    /// <summary>A current (not historical) node of the graph, or null.</summary>
    public OctopusResourceNode Find(string sourceId, OctopusResourceKind kind)
        => CurrentNode != null ? CurrentNode(sourceId, kind) : Graph.Resources.FirstOrDefault(r => r.Kind == kind && !r.IsHistorical && string.Equals(r.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
}
