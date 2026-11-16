using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.ChildInsert;

/// <summary>
/// One selected child to insert into the Squid owner an earlier import created, at <paramref name="Order"/> among the owner's rows.
/// Lives in Core, not Messages: it carries the graph and the id map.
/// </summary>
public sealed record OctopusImportChildInsert(OctopusResourceNode Resource, int OwnerDestinationId, int Order, OctopusImportIdMap IdMap, int SpaceId, OctopusResourceGraph Graph)
{
    /// <summary>A current (not historical) node of the graph, or null.</summary>
    public OctopusResourceNode Find(string sourceId, OctopusResourceKind kind)
        => Graph.Resources.FirstOrDefault(r => r.Kind == kind && !r.IsHistorical && string.Equals(r.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
}
