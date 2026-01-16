using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Events.Deployments.LifeCycle;
using Squid.Message.Models.Deployments;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Requests.Deployments.LifeCycle;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface ILifeCycleService : IScopedDependency
{
    Task<LifeCycleCreateEvent> CreateLifeCycleAsync(CreateLifeCycleCommand command, CancellationToken cancellationToken);
    
    Task<LifeCycleUpdatedEvent> UpdateLifeCycleAsync(UpdateLifeCycleCommand command, CancellationToken cancellationToken);
    
    Task<LifeCycleDeletedEvent> DeleteLifeCyclesAsync(DeleteLifeCyclesCommand command, CancellationToken cancellationToken);
    
    Task<GetLifeCycleResponse> GetLifecycleAsync(GetLifecycleRequest request, CancellationToken cancellationToken);
}

public class LifeCycleService(IMapper mapper, ILifeCycleDataProvider lifeCycleDataProvider)
    : ILifeCycleService
{
    public async Task<LifeCycleCreateEvent> CreateLifeCycleAsync(CreateLifeCycleCommand command, CancellationToken cancellationToken)
    {
        var retentionPolicies = command.LifecyclePhase.Phases.Select(x => x.ReleaseRetentionPolicy).Concat(command.LifecyclePhase.Phases.Select(x => x.TentacleRetentionPolicy)).ToList();
        var lifecycle = mapper.Map<Lifecycle>(command.LifecyclePhase.Lifecycle);
        var phases = mapper.Map<List<Phase>>(command.LifecyclePhase.Phases);
        
        retentionPolicies.Add(command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy);
        retentionPolicies.Add(command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy);

        await lifeCycleDataProvider.AddRetentionPoliciesAsync(mapper.Map<List<RetentionPolicy>>(retentionPolicies), cancellationToken: cancellationToken).ConfigureAwait(false);
        await lifeCycleDataProvider.AddLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
        phases.ForEach(x => x.LifecycleId = lifecycle.Id);
        await lifeCycleDataProvider.AddPhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new LifeCycleCreateEvent
        {
            Data = new CreateLifeCycleResponseData
            {
                LifecyclePhase = command.LifecyclePhase
            }
        };
    }

    public async Task<LifeCycleUpdatedEvent> UpdateLifeCycleAsync(UpdateLifeCycleCommand command, CancellationToken cancellationToken)
    {
        var retentionPoliciesIds = command.LifecyclePhase.Phases.Select(x => x.ReleaseRetentionPolicy.Id)
            .Concat(command.LifecyclePhase.Phases.Select(x => x.TentacleRetentionPolicy.Id))
            .Concat(new[] { command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy.Id, command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy.Id }).ToList();
        var lifecycle = await lifeCycleDataProvider.GetLifecycleByIdAsync(command.LifecyclePhase.Lifecycle.Id, cancellationToken).ConfigureAwait(false);
        var phases = await lifeCycleDataProvider.GetPhasesByIdAsync(command.LifecyclePhase.Phases.Select(x => x.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var retentionPolicies = await lifeCycleDataProvider.GetRetentionPoliciesByIdAsync(retentionPoliciesIds, cancellationToken: cancellationToken).ConfigureAwait(false);

        lifecycle = mapper.Map(command.LifecyclePhase.Lifecycle, lifecycle);
        phases = mapper.Map(command.LifecyclePhase.Phases, phases);
        retentionPolicies.ForEach(x =>
        {
            var rrpPhase = command.LifecyclePhase.Phases.FirstOrDefault(phase => x.Id == phase.ReleaseRetentionPolicy.Id);
            var trpPhase = command.LifecyclePhase.Phases.FirstOrDefault(phase => x.Id == phase.TentacleRetentionPolicy.Id);
            
            if (rrpPhase != null) x = mapper.Map(rrpPhase.ReleaseRetentionPolicy, x);
            
            if (trpPhase != null) x = mapper.Map(trpPhase.TentacleRetentionPolicy, x);

            if (command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy.Id == x.Id)
                x = mapper.Map(command.LifecyclePhase.Lifecycle.ReleaseRetentionPolicy, x);
            
            if (command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy.Id == x.Id)
                x = mapper.Map(command.LifecyclePhase.Lifecycle.TentacleRetentionPolicy, x);
        });

        await lifeCycleDataProvider.UpdateLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
        await lifeCycleDataProvider.UpdatePhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);
        await lifeCycleDataProvider.UpdateRetentionPoliciesAsync(retentionPolicies, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return new LifeCycleUpdatedEvent
        {
            Data = new UpdateLifeCycleResponseData
            {
                LifecyclePhase = command.LifecyclePhase
            }
        };
    }

    public async Task<LifeCycleDeletedEvent> DeleteLifeCyclesAsync(DeleteLifeCyclesCommand command, CancellationToken cancellationToken)
    {
        var lifecycles = await lifeCycleDataProvider.GetLifecyclesByIdAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await lifeCycleDataProvider.DeleteLifecyclesAsync(lifecycles, cancellationToken: cancellationToken).ConfigureAwait(false);

        var lifecycleIds = lifecycles.Select(x => x.Id).ToList();
        var failIds = command.Ids.Except(lifecycleIds).ToList();

        return new LifeCycleDeletedEvent
        {
            Data = new DeleteLifeCyclesResponseData
            {
                FailIds = failIds
            }
        };
    }

    public async Task<GetLifeCycleResponse> GetLifecycleAsync(GetLifecycleRequest request, CancellationToken cancellationToken)
    {
        var (count, lifecyclePhases) = await lifeCycleDataProvider.GetLifecyclePhasePagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

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

        var retentionPolicies = await lifeCycleDataProvider.GetRetentionPoliciesByIdAsync(retentionPolicyIds, cancellationToken: cancellationToken).ConfigureAwait(false);
        
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
        if (rrp != null) data.ReleaseRetentionPolicy = mapper.Map<RetentionPolicyDto>(rrp);
        
        var trp = retentionPolicies.FirstOrDefault(r => r.Id == data.TentacleRetentionPolicyId);
        if (trp != null) data.TentacleRetentionPolicy = mapper.Map<RetentionPolicyDto>(trp);
    }
}
