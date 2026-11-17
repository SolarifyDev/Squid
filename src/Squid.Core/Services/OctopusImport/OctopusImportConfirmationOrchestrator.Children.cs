using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.ChildInsert;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Children written with their owner, or inserted into an owner an earlier import created. Only a child the user selected reaches an
/// imported owner (the plan's choice rows are server-owned and validated). It is inserted at its Octopus position among the rows Squid
/// holds, writing only its own rows; a row of the same name that appeared in Squid since the plan was validated refuses it as stale, so
/// it is never added twice.
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    private async Task CreateChildAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        if (!execution.IsOwnerImportedBefore(resource))
        {
            MarkChildCreatedWithOwner(execution, resource);
            return;
        }

        await InsertIntoImportedOwnerAsync(execution, resource, ct).ConfigureAwait(false);
    }

    // The owner created now wrote it (a step's actions, a lifecycle's phases, a set's variables).
    private static void MarkChildCreatedWithOwner(ConfirmationExecutionContext execution, OctopusResourceNode resource)
    {
        if (resource.Kind == OctopusResourceKind.Variable)
        {
            MarkVariableHandled(execution, resource);
            return;
        }

        execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Created);
    }

    private async Task InsertIntoImportedOwnerAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        var inserter = ResolveInserter(resource);
        var ownerId = ResolveOwnerDestination(execution, resource);

        await EnsureNoNamesakeUnderImportedOwnerAsync(execution, resource, ownerId, ct).ConfigureAwait(false);

        var order = await PlaceAsync(execution, inserter, resource, ownerId, ct).ConfigureAwait(false);
        var inserted = await inserter.InsertAsync(new OctopusImportChildInsert(resource, ownerId, order, execution.IdMap, execution.DestinationSpaceId, execution.Request.Graph, execution.CurrentGraphNode), ct).ConfigureAwait(false);

        RecordInserted(execution, resource, inserted);
    }

    private IOctopusImportChildInserter ResolveInserter(OctopusResourceNode resource)
        => _childInserters.FirstOrDefault(i => i.Kind == resource.Kind) ?? throw MappingBlocked(resource, $"No Octopus {resource.Kind} can be added into an imported Squid resource by this server.");

    private static int ResolveOwnerDestination(ConfirmationExecutionContext execution, OctopusResourceNode resource)
    {
        var ownerKind = OctopusImportChildOwners.OwnerKind(resource.Kind);

        if (execution.IdMap.TryGetDestinationId(OctopusImportPriorMappingPolicy.OwnerSourceId(resource), ownerKind.ToString(), out var ownerId)) return ownerId;

        throw MappingBlocked(resource, $"The Squid {ownerKind} this {resource.Kind} belongs to has not been mapped.");
    }

    // Under the space lock, just before writing: a row of that name made in Squid after the plan was validated is never duplicated.
    private async Task EnsureNoNamesakeUnderImportedOwnerAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, int ownerId, CancellationToken ct)
    {
        if (_destinationDataProvider == null)
            throw MappingBlocked(resource, $"This server cannot check the imported Squid resource for a {resource.Kind} of the same name.");

        var children = await _destinationDataProvider.GetChildNamesAsync(execution.DestinationSpaceId, resource.Kind, [ownerId], ct).ConfigureAwait(false);

        if (!IsHeldInSquid(execution, resource, children)) return;

        throw StalePlan(execution, resource, $"Squid holds a {resource.Kind} named '{resource.Name}' there since this plan was validated; it is never added a second time. Re-run the preview.");
    }

    // The preview's rule: by name; a variable by name and scope, one Squid row each (rows written by this confirmation, or linked to
    // another Octopus variable, hold that one).
    private static bool IsHeldInSquid(ConfirmationExecutionContext execution, OctopusResourceNode resource, IReadOnlyList<OctopusImportDestinationChild> children)
    {
        if (resource.Kind != OctopusResourceKind.Variable) return children.Any(c => c.Name == resource.Name);

        return OctopusImportVariableMatching.Pair(UnmappedVariablesBeside(execution, resource), children.Where(c => !execution.IsClaimedVariableRow(c.Id)), execution.DestinationOf).ContainsKey(resource.SourceId);
    }

    private static IEnumerable<OctopusResourceNode> UnmappedVariablesBeside(ConfirmationExecutionContext execution, OctopusResourceNode variable)
        => execution.CurrentVariablesOf(variable.ParentSourceId).Where(r => !execution.IdMap.TryGetDestinationId(r, out _));

    // The variables written together with their set are recorded like any create, so one later deleted in Squid is told apart from one
    // added in Octopus. The set's rows are read back and paired by the same rule (the save returns no variable ids).
    private async Task MapVariablesWrittenWithSetAsync(ConfirmationExecutionContext execution, OctopusResourceNode set, int variableSetId, CancellationToken ct)
    {
        if (_destinationDataProvider == null) return;

        var rows = await _destinationDataProvider.GetChildNamesAsync(execution.DestinationSpaceId, OctopusResourceKind.Variable, [variableSetId], ct).ConfigureAwait(false);
        var variables = execution.CurrentResources.Where(r => r.Kind == OctopusResourceKind.Variable && string.Equals(r.ParentSourceId, set.SourceId, StringComparison.OrdinalIgnoreCase));

        foreach (var (sourceId, rowId) in OctopusImportVariableMatching.Pair(variables, rows, execution.DestinationOf))
            execution.AddCreated(execution.FindResource(sourceId, OctopusResourceKind.Variable), rowId);
    }

    private static async Task<int> PlaceAsync(ConfirmationExecutionContext execution, IOctopusImportChildInserter inserter, OctopusResourceNode resource, int ownerId, CancellationToken ct)
    {
        var orders = await inserter.SiblingOrdersAsync(ownerId, ct).ConfigureAwait(false);
        var owner = execution.FindResource(OctopusImportPriorMappingPolicy.OwnerSourceId(resource), OctopusImportChildOwners.OwnerKind(resource.Kind));
        var siblings = OctopusImportChildPlacement.OctopusSiblingIds(resource.Kind, owner);

        int? SquidOrderOf(string siblingId) => execution.IdMap.TryGetDestinationId(siblingId, resource.Kind.ToString(), out var id) && orders.TryGetValue(id, out var order) ? order : null;

        return OctopusImportChildPlacement.InsertionOrder(siblings, resource.SourceId, SquidOrderOf) ?? OctopusImportChildPlacement.AppendOrder(orders);
    }

    // Recorded like any create (the source map gets a row), and so are the rows written with it.
    private static void RecordInserted(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportInsertedChild inserted)
    {
        execution.AddDiagnostics(resource, inserted.Diagnostics);
        EnsureNoBlockers(inserted.Diagnostics);
        execution.AddCreated(resource, inserted.Id);

        foreach (var child in execution.CurrentResources.Where(r => string.Equals(r.ParentSourceId, resource.SourceId, StringComparison.OrdinalIgnoreCase)))
            if (inserted.Followers.TryGetValue(child.SourceId, out var childId)) execution.AddCreated(child, childId);
    }

    // A channel added into a project imported before: one of that name made in Squid since the plan was validated is never duplicated.
    // A Squid channel the source map links to another Octopus channel is that channel's (a renamed Octopus channel keeps it).
    private async Task EnsureNoChannelNamesakeAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, int projectId, CancellationToken ct)
    {
        var channels = await _channelDataProvider.GetChannelsByProjectIdAsync(projectId, ct).ConfigureAwait(false);

        if (!channels.Any(c => IsUnlinkedNamesake(execution, c, resource))) return;

        throw StalePlan(execution, resource, $"Squid holds a channel named '{resource.Name}' in this project since this plan was validated; it is never added a second time. Re-run the preview.");
    }

    private static bool IsUnlinkedNamesake(ConfirmationExecutionContext execution, Channel channel, OctopusResourceNode resource)
        => string.Equals(channel.Name?.Trim(), resource.Name?.Trim(), StringComparison.OrdinalIgnoreCase) && !IsLinkedToAnotherChannel(execution, channel.Id, resource.SourceId);

    private static bool IsLinkedToAnotherChannel(ConfirmationExecutionContext execution, int channelId, string sourceId)
    {
        var (channelType, ownId) = OctopusImportPriorMappings.Key(nameof(OctopusResourceKind.Channel), sourceId);

        return execution.Prior.Mappings.Any(m => m.Key.Type == channelType && m.Key.Id != ownId && m.Value.DestinationId == channelId)
               || execution.IdMap.Mappings.Any(m => m.SourceType == nameof(OctopusResourceKind.Channel) && m.DestinationId == channelId);
    }
}
