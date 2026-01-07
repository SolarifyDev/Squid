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
    
    Task<(int count, List<LifecyclePhaseDto> lifecyclePhases)> GetLifecyclePhasePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
    
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

public class LifeCycleDataProvider : ILifeCycleDataProvider
{
    private readonly IMapper _mapper;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRepository _repository;

    public LifeCycleDataProvider(IUnitOfWork unitOfWork, IRepository repository, IMapper mapper)
    {
        _mapper = mapper;
        _unitOfWork = unitOfWork;
        _repository = repository;
    }

    public async Task AddLifecycleAsync(Lifecycle lifeCycle, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(lifeCycle, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLifecycleAsync(Lifecycle lifeCycle, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(lifeCycle, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteLifecyclesAsync(List<Lifecycle> lifeCycles, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAllAsync(lifeCycles, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task GetLifecyclePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        
    }

    public async Task<(int count, List<LifecyclePhaseDto> lifecyclePhases)> GetLifecyclePhasePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = from lifecycle in _repository.Query<Lifecycle>()
            join phase in _repository.Query<Phase>() on lifecycle.Id equals phase.LifecycleId
            group new { phase, lifecycle } by lifecycle into grouped
            select new { Lifecycle = grouped.Key, Phases = grouped.Select(x=> x.phase) };

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
        {
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);
        }

        return (count, await query.Select(
            x => new LifecyclePhaseDto
            {
                Lifecycle = _mapper.Map<LifeCycleDto>(x.Lifecycle), Phases = _mapper.Map<List<PhaseDto>>(x.Phases)
            }).ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<Lifecycle> GetLifecycleByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _repository.QueryNoTracking<Lifecycle>(x => x.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Lifecycle>> GetLifecyclesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<Lifecycle>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddPhasesAsync(List<Phase> phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdatePhasesAsync(List<Phase> phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAllAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePhaseAsync(Phase phase, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(phase, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Phase>> GetPhasesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.QueryNoTracking<Phase>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task AddRetentionPolicyAsync(RetentionPolicy retentionPolicy, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(retentionPolicy, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task AddRetentionPoliciesAsync(List<RetentionPolicy> retentionPolicies, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(retentionPolicies, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task UpdateRetentionPoliciesAsync(List<RetentionPolicy> retentionPolicies, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAllAsync(retentionPolicies, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRetentionPolicyAsync(RetentionPolicy retentionPolicy, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteAsync(retentionPolicy, cancellationToken).ConfigureAwait(false);
        
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task<List<RetentionPolicy>> GetRetentionPoliciesByIdAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await _repository.Query<RetentionPolicy>(x => ids.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
