using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.ChildInsert;

/// <summary>
/// Pure: where a selected child goes among the Squid rows of its owner, so it keeps its Octopus position relative to the siblings Squid
/// already holds: right after the nearest preceding Octopus sibling Squid holds, else at the nearest following one, else at the end.
/// Siblings inserted earlier in the same confirmation count as held, so consecutive new siblings keep their Octopus order whatever order
/// they are inserted in.
/// </summary>
public static class OctopusImportChildPlacement
{
    /// <summary>The Squid order to insert at, or null when no Octopus sibling of the child is held in Squid (append).</summary>
    public static int? InsertionOrder(IReadOnlyList<string> octopusSiblingIdsInOrder, string newId, Func<string, int?> squidOrderOf)
    {
        var index = IndexOf(octopusSiblingIdsInOrder, newId);

        if (index < 0) return null;

        return PrecedingOrder(octopusSiblingIdsInOrder, index, squidOrderOf) + 1 ?? FollowingOrder(octopusSiblingIdsInOrder, index, squidOrderOf);
    }

    /// <summary>After the last row of the owner (the first order when it holds none).</summary>
    public static int AppendOrder(IReadOnlyDictionary<int, int> siblingOrders)
        => siblingOrders.Count == 0 ? 1 : siblingOrders.Values.Max() + 1;

    /// <summary>The child's siblings in Octopus order, as graph source ids: a process's steps, a step's actions, a lifecycle's phases.</summary>
    public static IReadOnlyList<string> OctopusSiblingIds(OctopusResourceKind childKind, OctopusResourceNode owner)
        => childKind switch
        {
            OctopusResourceKind.DeploymentStep => ChildIds(owner?.OwnerProjectId, owner?.SourceId, OctopusImportChildSourceIds.StepToken, owner?.GetSource<OctopusDeploymentProcessDto>()?.Steps?.Select(s => s.Id)),
            OctopusResourceKind.DeploymentAction => ChildIds(owner?.OwnerProjectId, owner?.SourceId, OctopusImportChildSourceIds.ActionToken, owner?.GetSource<OctopusDeploymentStepDto>()?.Actions?.Select(a => a.Id)),
            OctopusResourceKind.LifecyclePhase => ChildIds(owner?.SourceId, owner?.SourceId, OctopusImportChildSourceIds.PhaseToken, owner?.GetSource<OctopusLifecycleDto>()?.Phases?.Select(p => p.Id)),
            _ => []
        };

    // A process's and a step's children are qualified by their project; a lifecycle's phases by the lifecycle.
    private static List<string> ChildIds(string qualifierId, string parentSourceId, string childToken, IEnumerable<string> ids)
        => (ids ?? []).Select((id, index) => OctopusImportChildSourceIds.Qualify(qualifierId, parentSourceId, childToken, id, index)).ToList();

    /// <summary>The position of <paramref name="id"/> among <paramref name="ids"/> (ignoring case), or -1.</summary>
    public static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }

    private static int? PrecedingOrder(IReadOnlyList<string> ids, int index, Func<string, int?> squidOrderOf)
    {
        for (var i = index - 1; i >= 0; i--)
            if (squidOrderOf(ids[i]) is { } order) return order;

        return null;
    }

    private static int? FollowingOrder(IReadOnlyList<string> ids, int index, Func<string, int?> squidOrderOf)
    {
        for (var i = index + 1; i < ids.Count; i++)
            if (squidOrderOf(ids[i]) is { } order) return order;

        return null;
    }
}
