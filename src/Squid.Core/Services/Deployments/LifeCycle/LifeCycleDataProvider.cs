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
    
    Task<(int Count, List<LifecyclePhaseDto> LifecyclePhases)> GetLifecyclePhasePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
    
    Task<Lifecycle> GetLifecycleByIdAsync(int id, CancellationToken cancellationToken);
    
    Task<List<Lifecycle>> GetLifecyclesByIdAsync(List<int> ids, CancellationToken cancellationToken);

    Task AddPhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task AddPhasesAsync(List<Phase> phase, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task UpdatePhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task UpdatePhasesAsync(List<Phase> phase, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task DeletePhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<Phase>> GetPhasesByIdAsync(List<int> ids, CancellationToken cancellationToken);
    
    Task AddRetentionPolicyAsync(RetentionPolicy retentionPolicy, bool forceSave = true, CancellationToken cancellationToken = default);

    Task AddRetentionPoliciesAsync(List<RetentionPolicy> retentionPolicies, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateRetentionPoliciesAsync(List<RetentionPolicy> retentionPolicies, bool forceSave = true, CancellationToken cancellationToken = default);
    
    Task DeleteRetentionPolicyAsync(RetentionPolicy retentionPolicy, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<RetentionPolicy>> GetRetentionPoliciesByIdAsync(List<int> ids, CancellationToken cancellationToken);
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

    public async Task<(int Count, List<LifecyclePhaseDto> LifecyclePhases)> GetLifecyclePhasePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = from lifecycle in repository.Query<Lifecycle>()
            join phase in repository.Query<Phase>() on lifecycle.Id equals phase.LifecycleId
            group new { phase, lifecycle } by lifecycle into grouped
            select new { Lifecycle = grouped.Key, Phases = grouped.Select(x=> x.phase) };

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var phases = await query
            .Select(x => new LifecyclePhaseDto { Lifecycle = mapper.Map<LifeCycleDto>(x.Lifecycle), Phases = mapper.Map<List<PhaseDto>>(x.Phases) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        
        return (count, phases);
    }

    public async Task<Lifecycle> GetLifecycleByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<Lifecycle>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Lifecycle>> GetLifecyclesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<Lifecycle>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhasesAsync(List<Phase> phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAllAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhasesAsync(List<Phase> phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAllAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Phase>> GetPhasesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.QueryNoTracking<Phase>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task AddRetentionPolicyAsync(RetentionPolicy retentionPolicy, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(retentionPolicy, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task AddRetentionPoliciesAsync(List<RetentionPolicy> retentionPolicies, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAllAsync(retentionPolicies, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task UpdateRetentionPoliciesAsync(List<RetentionPolicy> retentionPolicies, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAllAsync(retentionPolicies, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRetentionPolicyAsync(RetentionPolicy retentionPolicy, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAsync(retentionPolicy, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task<List<RetentionPolicy>> GetRetentionPoliciesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<RetentionPolicy>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
