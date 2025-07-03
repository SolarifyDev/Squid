using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Domain.Deployments;
using Squid.Message.Events.Deployments.LifeCycle;
using Squid.Message.Models.Deployments;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Requests.Deployments.LifeCycle;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface ILifeCycleService : IScopedDependency
{
    Task<CreateLifeCycleEvent> CreateLifeCycleAsync(CreateLifeCycleCommand command, CancellationToken cancellationToken);
    
    Task<UpdateLifeCycleEvent> UpdateLifeCycleAsync(UpdateLifeCycleCommand command, CancellationToken cancellationToken);
    
    Task<DeleteLifeCycleEvent> DeleteLifeCyclesAsync(DeleteLifeCyclesCommand command, CancellationToken cancellationToken);
    
    Task<GetLifeCycleResponse> GetLifecycleAsync(GetLifecycleRequest request, CancellationToken cancellationToken);
}

public class LifeCycleService : ILifeCycleService
{
    private readonly IMapper _mapper;
    private readonly ILifeCycleDataProvider _lifeCycleDataProvider;

    public LifeCycleService(IMapper mapper, ILifeCycleDataProvider lifeCycleDataProvider)
    {
        _mapper = mapper;
        _lifeCycleDataProvider = lifeCycleDataProvider;
    }

    public async Task<CreateLifeCycleEvent> CreateLifeCycleAsync(CreateLifeCycleCommand command, CancellationToken cancellationToken)
    {
        command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicyId = command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy.Id = Guid.NewGuid();
        command.LifecyclePhase.Lifecycle.TentacleRetentionPolicyId = command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy.Id = Guid.NewGuid();
        command.LifecyclePhase.Phases.ForEach(
            x =>
            {
                x.ReleaseRetentionPolicyId = x.ReleaseRetentionPolicy.Id = Guid.NewGuid();
                x.TentacleRetentionPolicyId = x.TentacleRetentionPolicy.Id = Guid.NewGuid();
            });

        var retentionPolicies = command.LifecyclePhase.Phases.Select(x => x.ReleaseRetentionPolicy).Concat(command.LifecyclePhase.Phases.Select(x => x.TentacleRetentionPolicy)).ToList();
        var lifecycle = _mapper.Map<Lifecycle>(command.LifecyclePhase.Lifecycle);
        var phases = _mapper.Map<List<Phase>>(command.LifecyclePhase.Phases);
        
        retentionPolicies.Add(command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy);
        retentionPolicies.Add(command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy);

        await _lifeCycleDataProvider.AddRetentionPoliciesAsync(_mapper.Map<List<RetentionPolicy>>(retentionPolicies), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _lifeCycleDataProvider.AddLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
        phases.ForEach(x => x.LifecycleId = lifecycle.Id);
        await _lifeCycleDataProvider.AddPhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CreateLifeCycleEvent
        {
            Data = new CreateLifeCycleResponseData
            {
                LifecyclePhase = command.LifecyclePhase
            }
        };
    }

    public async Task<UpdateLifeCycleEvent> UpdateLifeCycleAsync(UpdateLifeCycleCommand command, CancellationToken cancellationToken)
    {
        var retentionPoliciesIds = command.LifecyclePhase.Phases.Select(x => x.ReleaseRetentionPolicy.Id)
            .Concat(command.LifecyclePhase.Phases.Select(x => x.TentacleRetentionPolicy.Id))
            .Concat([command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy.Id, command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy.Id]).ToList();
        var lifecycle = await _lifeCycleDataProvider.GetLifecycleByIdAsync(command.LifecyclePhase.Lifecycle.Id, cancellationToken).ConfigureAwait(false);
        var phases = await _lifeCycleDataProvider.GetPhasesByIdAsync(command.LifecyclePhase.Phases.Select(x => x.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var retentionPolicies = await _lifeCycleDataProvider.GetRetentionPoliciesByIdAsync(retentionPoliciesIds, cancellationToken: cancellationToken).ConfigureAwait(false);

        lifecycle = _mapper.Map(command.LifecyclePhase.Lifecycle, lifecycle);
        phases = _mapper.Map(command.LifecyclePhase.Phases, phases);
        retentionPolicies.ForEach(x =>
        {
            var rrpPhase = command.LifecyclePhase.Phases.FirstOrDefault(phase => x.Id == phase.ReleaseRetentionPolicy.Id);
            var trpPhase = command.LifecyclePhase.Phases.FirstOrDefault(phase => x.Id == phase.TentacleRetentionPolicy.Id);
            
            if (rrpPhase != null) x = _mapper.Map(rrpPhase.ReleaseRetentionPolicy, x);
            
            if (trpPhase != null) x = _mapper.Map(trpPhase.TentacleRetentionPolicy, x);

            if (command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy.Id == x.Id)
                x = _mapper.Map(command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy, x);
            
            if (command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy.Id == x.Id)
                x = _mapper.Map(command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy, x);
        });

        await _lifeCycleDataProvider.UpdateLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _lifeCycleDataProvider.UpdatePhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _lifeCycleDataProvider.UpdateRetentionPoliciesAsync(retentionPolicies, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return new UpdateLifeCycleEvent
        {
            Data = new UpdateLifeCycleResponseData
            {
                LifecyclePhase = command.LifecyclePhase
            }
        };
    }

    public async Task<DeleteLifeCycleEvent> DeleteLifeCyclesAsync(DeleteLifeCyclesCommand command, CancellationToken cancellationToken)
    {
        var failIds = new List<Guid>();
        var lifecycles = await _lifeCycleDataProvider.GetLifecyclesByIdAsync(command.Ids, cancellationToken).ConfigureAwait(false);
        
        if (lifecycles.Count != command.Ids.Count) failIds.AddRange(command.Ids.Except(lifecycles.Select(x => x.Id)));

        await _lifeCycleDataProvider.DeleteLifecyclesAsync(lifecycles, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return new DeleteLifeCycleEvent
        {
            Data = new DeleteLifeCyclesResponseData
            {
                FailIds = failIds
            }
        };
    }

    public async Task<GetLifeCycleResponse> GetLifecycleAsync(GetLifecycleRequest request, CancellationToken cancellationToken)
    {
        var (count, lifecyclePhases) = await _lifeCycleDataProvider.GetLifecyclePhasePagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        lifecyclePhases = await EnhanceRetentionPolicyToLifecycleAsync(lifecyclePhases, cancellationToken).ConfigureAwait(false);

        return new GetLifeCycleResponse
        {
            Data = new GetLifeCycleResponseData
            {
                Count = count, LifeCycles = lifecyclePhases
            }
        };
    }

    private async Task<List<LifecyclePhaseDto>> EnhanceRetentionPolicyToLifecycleAsync(List<LifecyclePhaseDto> lifecyclePhases, CancellationToken cancellationToken)
    {
        var retentionPolicyIds = lifecyclePhases.SelectMany(x => x.Phases.Select(p => p.ReleaseRetentionPolicyId))
            .Concat(lifecyclePhases.SelectMany(x => x.Phases.Select(p => p.TentacleRetentionPolicyId))).ToList();
        
        retentionPolicyIds.AddRange(lifecyclePhases.Select(x => x.Lifecycle.ReleaseRetentionPolicyId).Concat(lifecyclePhases.Select(x => x.Lifecycle.TentacleRetentionPolicyId)));

        var retentionPolicies = await _lifeCycleDataProvider.GetRetentionPoliciesByIdAsync(retentionPolicyIds, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        foreach (var lifecyclePhase in lifecyclePhases)
        {
            GetRetentionPolicyById(retentionPolicies, lifecyclePhase.Lifecycle);
            
            lifecyclePhase.Phases.ForEach(x => { GetRetentionPolicyById(retentionPolicies, x); });
        }

        return lifecyclePhases;
    }

    private void GetRetentionPolicyById<T>(List<RetentionPolicy> retentionPolicies, T data) where T : IHasDualRetentionPolicies
    {
        var rrp = retentionPolicies.FirstOrDefault(r => r.Id == data.ReleaseRetentionPolicyId);
        if (rrp != null) data.ReleaseRetentionPolicy = _mapper.Map<RetentionPolicyDto>(rrp);
        
        var trp = retentionPolicies.FirstOrDefault(r => r.Id == data.TentacleRetentionPolicyId);
        if (trp != null) data.TentacleRetentionPolicy = _mapper.Map<RetentionPolicyDto>(trp);
    }
}