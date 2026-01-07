using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Process;
using Squid.Message.Events.Deployments.Process;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentProcessService : IScopedDependency
{
    Task<DeploymentProcessCreatedEvent> CreateDeploymentProcessAsync(CreateDeploymentProcessCommand command, CancellationToken cancellationToken);
    
    Task<DeploymentProcessUpdatedEvent> UpdateDeploymentProcessAsync(UpdateDeploymentProcessCommand command, CancellationToken cancellationToken);
    
    Task DeleteDeploymentProcessAsync(int id, CancellationToken cancellationToken);
    
    Task<DeploymentProcessDto> GetDeploymentProcessByIdAsync(int id, CancellationToken cancellationToken);
    
    Task<GetDeploymentProcessesResponse> GetDeploymentProcessesAsync(GetDeploymentProcessesRequest request, CancellationToken cancellationToken);
}

public class DeploymentProcessService : IDeploymentProcessService
{
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentStepPropertyDataProvider _stepPropertyDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IMapper _mapper;

    public DeploymentProcessService(
        IMapper mapper,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider)
    {
        _mapper = mapper;
        _stepDataProvider = stepDataProvider;
        _actionDataProvider = actionDataProvider;
        _processDataProvider = processDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
    }

    public async Task<DeploymentProcessCreatedEvent> CreateDeploymentProcessAsync(CreateDeploymentProcessCommand command, CancellationToken cancellationToken)
    {
        var process = _mapper.Map<DeploymentProcess>(command);

        process.Version = await _processDataProvider
            .GetNextVersionAsync(command.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        process.LastModified = DateTimeOffset.UtcNow;

        await _processDataProvider.AddDeploymentProcessAsync(process, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DeploymentProcessCreatedEvent
        {
            DeploymentProcess = _mapper.Map<DeploymentProcessDto>(process)
        };
    }

    public async Task<DeploymentProcessUpdatedEvent> UpdateDeploymentProcessAsync(UpdateDeploymentProcessCommand command, CancellationToken cancellationToken)
    {
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (process == null)
        {
            throw new InvalidOperationException($"DeploymentProcess with id {command.Id} not found");
        }

        _mapper.Map(command, process);
        process.LastModified = DateTimeOffset.UtcNow;

        await _processDataProvider.UpdateDeploymentProcessAsync(process, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DeploymentProcessUpdatedEvent
        {
            DeploymentProcess = _mapper.Map<DeploymentProcessDto>(process)
        };
    }

    public async Task DeleteDeploymentProcessAsync(int id, CancellationToken cancellationToken)
    {
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (process == null)
        {
            throw new InvalidOperationException($"DeploymentProcess with id {id} not found");
        }

        await _stepDataProvider.DeleteDeploymentStepsByProcessIdAsync(id, cancellationToken).ConfigureAwait(false);
        await _processDataProvider.DeleteDeploymentProcessAsync(process, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentProcessDto> GetDeploymentProcessByIdAsync(int id, CancellationToken cancellationToken)
    {
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (process == null)
        {
            throw new InvalidOperationException($"DeploymentProcess with id {id} not found");
        }

        return await BuildDeploymentProcessDtoAsync(process, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetDeploymentProcessesResponse> GetDeploymentProcessesAsync(GetDeploymentProcessesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _processDataProvider.GetDeploymentProcessPagingAsync(
            request.SpaceId, request.ProjectId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        var processDtos = new List<DeploymentProcessDto>();

        foreach (var process in data)
        {
            var dto = await BuildDeploymentProcessDtoAsync(process, cancellationToken).ConfigureAwait(false);
            processDtos.Add(dto);
        }

        return new GetDeploymentProcessesResponse
        {
            Data = new GetDeploymentProcessesResponseData
            {
                Count = count,
                DeploymentProcesses = processDtos
            }
        };
    }

    private async Task<DeploymentProcessDto> BuildDeploymentProcessDtoAsync(DeploymentProcess process, CancellationToken cancellationToken)
    {
        var processDto = _mapper.Map<DeploymentProcessDto>(process);

        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(process.Id, cancellationToken).ConfigureAwait(false);

        var stepIds = steps.Select(s => s.Id).ToList();

        var allStepProperties = await _stepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);
        var stepPropertiesDict = allStepProperties.GroupBy(p => p.StepId).ToDictionary(g => g.Key, g => g.ToList());

        var allActions = await _actionDataProvider.GetDeploymentActionsByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);
        var actionsDict = allActions.GroupBy(a => a.StepId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var step in steps)
        {
            var stepDto = _mapper.Map<DeploymentStepDto>(step);

            var stepProperties = stepPropertiesDict.TryGetValue(step.Id, out var sps) ? sps : new List<DeploymentStepProperty>();
            stepDto.Properties = _mapper.Map<List<DeploymentStepPropertyDto>>(stepProperties);

            var actions = actionsDict.TryGetValue(step.Id, out var acts) ? acts : new List<DeploymentAction>();
            stepDto.Actions = new List<DeploymentActionDto>();

            foreach (var action in actions)
            {
                var actionDto = await MapActionWithRelatedDataAsync(action, cancellationToken).ConfigureAwait(false);
                stepDto.Actions.Add(actionDto);
            }

            processDto.Steps.Add(stepDto);
        }

        return processDto;
    }
    
    private async Task<DeploymentActionDto> MapActionWithRelatedDataAsync(DeploymentAction action, CancellationToken cancellationToken)
    {
        var actionDto = _mapper.Map<DeploymentActionDto>(action);

        var properties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Properties = _mapper.Map<List<DeploymentActionPropertyDto>>(properties);

        return actionDto;
    }
}
