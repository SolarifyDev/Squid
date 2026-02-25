using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Enums.Deployments;
using Squid.Message.Events.Deployments.LifeCycle;
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
        var lifecycle = mapper.Map<Lifecycle>(command.LifecyclePhase.Lifecycle);
        var phases = mapper.Map<List<LifecyclePhase>>(command.LifecyclePhase.Phases);

        await lifeCycleDataProvider.AddLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);

        phases.ForEach(x => x.LifecycleId = lifecycle.Id);
        await lifeCycleDataProvider.AddPhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);

        await InsertLifecyclePhaseEnvironmentsAsync(command.LifecyclePhase.Phases, phases, cancellationToken).ConfigureAwait(false);

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
        var lifecycle = await lifeCycleDataProvider.GetLifecycleByIdAsync(command.LifecyclePhase.Lifecycle.Id, cancellationToken).ConfigureAwait(false);
        var phases = await lifeCycleDataProvider.GetPhasesByIdAsync(command.LifecyclePhase.Phases.Select(x => x.Id).ToList(), cancellationToken).ConfigureAwait(false);

        lifecycle = mapper.Map(command.LifecyclePhase.Lifecycle, lifecycle);
        phases = mapper.Map(command.LifecyclePhase.Phases, phases);

        await lifeCycleDataProvider.UpdateLifecycleAsync(lifecycle, cancellationToken: cancellationToken).ConfigureAwait(false);
        await lifeCycleDataProvider.UpdatePhasesAsync(phases, cancellationToken: cancellationToken).ConfigureAwait(false);

        await ReplaceLifecyclePhaseEnvironmentsAsync(command.LifecyclePhase.Phases, phases, cancellationToken).ConfigureAwait(false);

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

        await PopulateLifecyclePhaseEnvironmentTargetIdsAsync(lifecyclePhases, cancellationToken).ConfigureAwait(false);

        return new GetLifeCycleResponse
        {
            Data = new GetLifeCycleResponseData
            {
                Count = count, LifeCycles = lifecyclePhases
            }
        };
    }

    private async Task PopulateLifecyclePhaseEnvironmentTargetIdsAsync(List<LifecycleDetailDto> lifecycleDetails, CancellationToken cancellationToken)
    {
        var allPhaseIds = lifecycleDetails.SelectMany(lp => lp.Phases.Select(p => p.Id)).ToList();
        if (allPhaseIds.Count == 0) return;

        var phaseEnvironments = await lifeCycleDataProvider.GetPhaseEnvironmentsByPhaseIdsAsync(allPhaseIds, cancellationToken).ConfigureAwait(false);

        var grouped = phaseEnvironments.GroupBy(pe => pe.PhaseId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var lp in lifecycleDetails)
        {
            foreach (var phase in lp.Phases)
            {
                if (!grouped.TryGetValue(phase.Id, out var envs)) continue;

                phase.AutomaticDeploymentTargetIds = envs
                    .Where(e => e.TargetType == LifecyclePhaseEnvironmentTargetType.Automatic)
                    .Select(e => e.EnvironmentId).ToList();

                phase.OptionalDeploymentTargetIds = envs
                    .Where(e => e.TargetType == LifecyclePhaseEnvironmentTargetType.Optional)
                    .Select(e => e.EnvironmentId).ToList();
            }
        }
    }

    private async Task InsertLifecyclePhaseEnvironmentsAsync(List<LifecyclePhaseDto> phaseDtos, List<LifecyclePhase> phases, CancellationToken cancellationToken)
    {
        var phaseEnvironments = BuildLifecyclePhaseEnvironments(phaseDtos, phases);
        if (phaseEnvironments.Count == 0) return;

        await lifeCycleDataProvider.AddPhaseEnvironmentsAsync(phaseEnvironments, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceLifecyclePhaseEnvironmentsAsync(List<LifecyclePhaseDto> phaseDtos, List<LifecyclePhase> phases, CancellationToken cancellationToken)
    {
        foreach (var phase in phases)
        {
            await lifeCycleDataProvider.DeletePhaseEnvironmentsByPhaseIdAsync(phase.Id, cancellationToken).ConfigureAwait(false);
        }

        await InsertLifecyclePhaseEnvironmentsAsync(phaseDtos, phases, cancellationToken).ConfigureAwait(false);
    }

    private static List<LifecyclePhaseEnvironment> BuildLifecyclePhaseEnvironments(List<LifecyclePhaseDto> phaseDtos, List<LifecyclePhase> phases)
    {
        var result = new List<LifecyclePhaseEnvironment>();

        for (var i = 0; i < phaseDtos.Count && i < phases.Count; i++)
        {
            var dto = phaseDtos[i];
            var phaseId = phases[i].Id;

            result.AddRange(dto.AutomaticDeploymentTargetIds.Select(envId => new LifecyclePhaseEnvironment
            {
                PhaseId = phaseId,
                EnvironmentId = envId,
                TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
            }));

            result.AddRange(dto.OptionalDeploymentTargetIds.Select(envId => new LifecyclePhaseEnvironment
            {
                PhaseId = phaseId,
                EnvironmentId = envId,
                TargetType = LifecyclePhaseEnvironmentTargetType.Optional
            }));
        }

        return result;
    }
}
