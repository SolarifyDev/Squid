using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.LifeCycle;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface ILifeCycleDataProvider : IScopedDependency
{
    Task AddLifecycleAsync(Lifecycle lifeCycle, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateLifecycleAsync(Lifecycle lifeCycle, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteLifecyclesAsync(List<Lifecycle> lifeCycles, bool forceSave = true, CancellationToken cancellationToken = default);

    Task GetLifecyclePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<(int Count, List<LifecycleDetailDto> LifecycleDetails)> GetLifecyclePhasePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<Lifecycle> GetLifecycleByIdAsync(int id, CancellationToken cancellationToken);

    Task<List<Lifecycle>> GetLifecyclesByIdAsync(List<int> ids, CancellationToken cancellationToken);

    Task AddPhaseAsync(LifecyclePhase phase, bool forceSave = true, CancellationToken cancellationToken = default);

    Task AddPhasesAsync(List<LifecyclePhase> phases, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdatePhaseAsync(LifecyclePhase phase, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdatePhasesAsync(List<LifecyclePhase> phases, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeletePhaseAsync(LifecyclePhase phase, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<LifecyclePhase>> GetPhasesByIdAsync(List<int> ids, CancellationToken cancellationToken);

    Task<List<LifecyclePhase>> GetPhasesByLifecycleIdAsync(int lifecycleId, CancellationToken cancellationToken);

    Task<List<LifecyclePhase>> GetPhasesByLifecycleIdsAsync(List<int> lifecycleIds, CancellationToken cancellationToken);

    Task<List<LifecyclePhaseEnvironment>> GetPhaseEnvironmentsByPhaseIdsAsync(List<int> phaseIds, CancellationToken cancellationToken);

    Task AddPhaseEnvironmentsAsync(List<LifecyclePhaseEnvironment> phaseEnvironments, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeletePhaseEnvironmentsByPhaseIdAsync(int phaseId, CancellationToken cancellationToken);
}

public class LifeCycleDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper)
    : ILifeCycleDataProvider
{
    public async Task AddLifecycleAsync(Lifecycle lifeCycle, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(lifeCycle, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLifecycleAsync(Lifecycle lifeCycle, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(lifeCycle, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteLifecyclesAsync(List<Lifecycle> lifeCycles, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(lifeCycles, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task GetLifecyclePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
    }

    public async Task<(int Count, List<LifecycleDetailDto> LifecycleDetails)> GetLifecyclePhasePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = from lifecycle in repository.Query<Lifecycle>()
            join phase in repository.Query<LifecyclePhase>() on lifecycle.Id equals phase.LifecycleId
            group new { phase, lifecycle } by lifecycle into grouped
            select new { Lifecycle = grouped.Key, Phases = grouped.Select(x => x.phase) };

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var results = await query
            .Select(x => new LifecycleDetailDto { Lifecycle = mapper.Map<LifeCycleDto>(x.Lifecycle), Phases = mapper.Map<List<LifecyclePhaseDto>>(x.Phases) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, results);
    }

    public async Task<Lifecycle> GetLifecycleByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<Lifecycle>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Lifecycle>> GetLifecyclesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<Lifecycle>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhaseAsync(LifecyclePhase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(phase, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhasesAsync(List<LifecyclePhase> phases, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAllAsync(phases, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhaseAsync(LifecyclePhase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(phase, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhasesAsync(List<LifecyclePhase> phases, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAllAsync(phases, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePhaseAsync(LifecyclePhase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAsync(phase, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<LifecyclePhase>> GetPhasesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<LifecyclePhase>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<LifecyclePhase>> GetPhasesByLifecycleIdAsync(int lifecycleId, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<LifecyclePhase>(x => x.LifecycleId == lifecycleId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<LifecyclePhase>> GetPhasesByLifecycleIdsAsync(List<int> lifecycleIds, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<LifecyclePhase>(x => lifecycleIds.Contains(x.LifecycleId)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<LifecyclePhaseEnvironment>> GetPhaseEnvironmentsByPhaseIdsAsync(List<int> phaseIds, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<LifecyclePhaseEnvironment>(x => phaseIds.Contains(x.PhaseId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhaseEnvironmentsAsync(List<LifecyclePhaseEnvironment> phaseEnvironments, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAllAsync(phaseEnvironments, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePhaseEnvironmentsByPhaseIdAsync(int phaseId, CancellationToken cancellationToken)
    {
        var existing = await repository.Query<LifecyclePhaseEnvironment>(x => x.PhaseId == phaseId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (existing.Count > 0)
        {
            await repository.DeleteAllAsync(existing, cancellationToken).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
