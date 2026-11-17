using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.ChildInsert.Inserters;

/// <summary>A lifecycle phase added into an imported lifecycle: mapped the way the lifecycle's phases are, written on its own.</summary>
public sealed class PhaseInserter(IOctopusImportLifecycleMapper lifecycleMapper, ILifeCycleService lifeCycleService, IRepository repository) : IOctopusImportChildInserter
{
    public OctopusResourceKind Kind => OctopusResourceKind.LifecyclePhase;

    public async Task<IReadOnlyDictionary<int, int>> SiblingOrdersAsync(int ownerId, CancellationToken ct)
        => await repository.QueryNoTracking<LifecyclePhase>(p => p.LifecycleId == ownerId).ToDictionaryAsync(p => p.Id, p => p.SortOrder, ct).ConfigureAwait(false);

    public async Task<OctopusImportInsertedChild> InsertAsync(OctopusImportChildInsert insert, CancellationToken ct)
    {
        var lifecycle = insert.Find(insert.Resource.ParentSourceId, OctopusResourceKind.Lifecycle);
        var mapping = lifecycleMapper.MapPhase(lifecycle, PhaseIndex(lifecycle, insert.Resource.SourceId), insert.IdMap);

        if (OctopusImportSiblingDiagnostics.HasBlocker(mapping.Diagnostics)) return OctopusImportInsertedChild.Blocked(mapping.Diagnostics);

        var id = await lifeCycleService.InsertPhaseAsync(insert.OwnerDestinationId, mapping.Phase, insert.Order, ct).ConfigureAwait(false);

        return new OctopusImportInsertedChild(id, mapping.Diagnostics, new Dictionary<string, int>());
    }

    private static int PhaseIndex(OctopusResourceNode lifecycle, string phaseSourceId)
        => OctopusImportChildPlacement.IndexOf(OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.LifecyclePhase, lifecycle), phaseSourceId);
}
