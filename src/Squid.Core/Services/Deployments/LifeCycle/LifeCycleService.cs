using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Domain.Deployments;
using Squid.Message.Events.Deployments.LifeCycle;
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
        var lifecycle = _mapper.Map<Lifecycle>(command.LifecyclePhase.Lifecycle);
        var phases = _mapper.Map<List<Phase>>(command.LifecyclePhase.Phases);

        await _lifeCycleDataProvider.AddLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        var lifecycle = await _lifeCycleDataProvider.GetLifecycleByIdAsync(command.LifecyclePhase.Lifecycle.Id, cancellationToken).ConfigureAwait(false);
        var phases = await _lifeCycleDataProvider.GetPhasesByIdAsync(command.LifecyclePhase.Phases.Select(x => x.Id).ToList(), cancellationToken).ConfigureAwait(false);

        lifecycle = _mapper.Map(command.LifecyclePhase.Lifecycle, lifecycle);
        phases = _mapper.Map(command.LifecyclePhase.Phases, phases);

        await _lifeCycleDataProvider.UpdateLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _lifeCycleDataProvider.UpdatePhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);
        
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
        var lifecycles = await _lifeCycleDataProvider.GetLifecycleByIdAsync(command.Ids, cancellationToken).ConfigureAwait(false);
        
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

        return new GetLifeCycleResponse
        {
            Data = new GetLifeCycleResponseData
            {
                Count = count, LifeCycles = lifecyclePhases
            }
        };
    }
}