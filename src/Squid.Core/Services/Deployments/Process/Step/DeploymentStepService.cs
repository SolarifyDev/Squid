using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Events.Deployments.Step;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Requests.Deployments.Process.Step;

namespace Squid.Core.Services.Deployments.Process.Step;

public interface IDeploymentStepService : IScopedDependency
{
    Task<DeploymentStepCreatedEvent> CreateDeploymentStepAsync(CreateDeploymentStepCommand command, CancellationToken cancellationToken);

    Task<DeploymentStepUpdatedEvent> UpdateDeploymentStepAsync(UpdateDeploymentStepCommand command, CancellationToken cancellationToken);

    Task<DeploymentStepDeletedEvent> DeleteDeploymentStepsAsync(DeleteDeploymentStepCommand command, CancellationToken cancellationToken);

    Task<GetDeploymentStepResponse> GetDeploymentStepByIdAsync(int id, CancellationToken cancellationToken);

    Task<GetDeploymentStepsResponse> GetDeploymentStepsAsync(GetDeploymentStepsRequest request, CancellationToken cancellationToken);
}

public class DeploymentStepService : IDeploymentStepService
{
    private readonly IMapper _mapper;
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentStepPropertyDataProvider _stepPropertyDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;

    public DeploymentStepService(
        IMapper mapper,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider)
    {
        _mapper = mapper;
        _stepDataProvider = stepDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
    }

    public async Task<DeploymentStepCreatedEvent> CreateDeploymentStepAsync(CreateDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        var step = _mapper.Map<DeploymentStep>(command.Step);
        
        step.ProcessId = command.ProcessId;
        step.CreatedAt = DateTimeOffset.UtcNow;
        step.StepOrder = await ResolveNextStepOrderAsync(command.ProcessId, cancellationToken).ConfigureAwait(false);

        await _stepDataProvider.AddDeploymentStepAsync(step, false, cancellationToken).ConfigureAwait(false);

        if (command.Step.Properties?.Any() == true)
        {
            var properties = _mapper.Map<List<DeploymentStepProperty>>(command.Step.Properties);
            
            properties.ForEach(p => p.StepId = step.Id);

            await _stepPropertyDataProvider.AddDeploymentStepPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
        }

        if (command.Step.Actions?.Any() == true)
        {
            await CreateActionsAsync(step.Id, command.Step.Actions, cancellationToken).ConfigureAwait(false);
        }

        var stepWithRelatedData = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);

        return new DeploymentStepCreatedEvent { Data = stepWithRelatedData };
    }

    public async Task<DeploymentStepUpdatedEvent> UpdateDeploymentStepAsync(UpdateDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        var step = await _stepDataProvider.GetDeploymentStepByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        
        if (step == null) throw new DeploymentEntityNotFoundException("DeploymentStep", command.Id);

        _mapper.Map(command.Step, step);

        await _stepDataProvider.UpdateDeploymentStepAsync(step, false, cancellationToken).ConfigureAwait(false);

        await _stepPropertyDataProvider.DeleteDeploymentStepPropertiesByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);

        if (command.Step.Properties?.Any() == true)
        {
            var properties = _mapper.Map<List<DeploymentStepProperty>>(command.Step.Properties);
            
            properties.ForEach(p => p.StepId = step.Id);

            await _stepPropertyDataProvider.AddDeploymentStepPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
        }

        await _actionDataProvider.DeleteDeploymentActionsByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);

        if (command.Step.Actions?.Any() == true)
        {
            await CreateActionsAsync(step.Id, command.Step.Actions, cancellationToken).ConfigureAwait(false);
        }

        var stepWithRelatedData = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);

        return new DeploymentStepUpdatedEvent { Data = stepWithRelatedData };
    }

    public async Task<DeploymentStepDeletedEvent> DeleteDeploymentStepsAsync(DeleteDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        var steps = await _stepDataProvider.GetDeploymentStepsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _stepDataProvider.DeleteDeploymentStepsAsync(steps, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DeploymentStepDeletedEvent
        {
            Data = new DeleteDeploymentStepResponseData
            {
                FailIds = command.Ids.Except(steps.Select(s => s.Id)).ToList()
            }
        };
    }

    public async Task<GetDeploymentStepResponse> GetDeploymentStepByIdAsync(int id, CancellationToken cancellationToken)
    {
        var step = await GetStepWithRelatedDataAsync(id, cancellationToken).ConfigureAwait(false);
        
        if (step == null) throw new DeploymentEntityNotFoundException("DeploymentStep", id);
        
        return new GetDeploymentStepResponse { Data = step };
    }

    public async Task<GetDeploymentStepsResponse> GetDeploymentStepsAsync(GetDeploymentStepsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _stepDataProvider.GetDeploymentStepPagingAsync(
            request.ProcessId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        var steps = new List<DeploymentStepDto>();

        foreach (var step in data)
        {
            var stepDto = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);
            
            steps.Add(stepDto);
        }

        return new GetDeploymentStepsResponse
        {
            Data = new GetDeploymentStepsResponseData { Count = count, Steps = steps }
        };
    }

    private async Task<DeploymentStepDto> GetStepWithRelatedDataAsync(int stepId, CancellationToken cancellationToken)
    {
        var step = await _stepDataProvider.GetDeploymentStepByIdAsync(stepId, cancellationToken).ConfigureAwait(false);
        
        if (step == null) return null;

        var mappedStep = _mapper.Map<DeploymentStepDto>(step);

        var stepProperties = await _stepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);
        mappedStep.Properties = _mapper.Map<List<DeploymentStepPropertyDto>>(stepProperties);

        var actions = await _actionDataProvider.GetDeploymentActionsByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);
        mappedStep.Actions = new List<DeploymentActionDto>();

        foreach (var action in actions)
        {
            var actionDto = await MapActionWithRelatedDataAsync(action, cancellationToken).ConfigureAwait(false);
            
            mappedStep.Actions.Add(actionDto);
        }

        return mappedStep;
    }

    private async Task<DeploymentActionDto> MapActionWithRelatedDataAsync(DeploymentAction action, CancellationToken cancellationToken)
    {
        var actionDto = _mapper.Map<DeploymentActionDto>(action);

        var properties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        
        actionDto.Properties = _mapper.Map<List<DeploymentActionPropertyDto>>(properties);

        return actionDto;
    }

    private async Task<int> ResolveNextStepOrderAsync(int processId, CancellationToken cancellationToken)
    {
        var existingSteps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(processId, cancellationToken).ConfigureAwait(false);

        return existingSteps.Count == 0 ? 1 : existingSteps.Max(s => s.StepOrder) + 1;
    }

    private async Task CreateActionsAsync(int stepId, List<DeploymentActionDto> actions, CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            var mappedAction = _mapper.Map<DeploymentAction>(action);
            
            mappedAction.StepId = stepId;
            mappedAction.CreatedAt = DateTimeOffset.UtcNow;

            await _actionDataProvider.AddDeploymentActionAsync(mappedAction, true, cancellationToken).ConfigureAwait(false);

            if (action.Properties?.Any() != true) continue;

            var properties = _mapper.Map<List<DeploymentActionProperty>>(action.Properties);
            
            properties.ForEach(p => p.ActionId = mappedAction.Id);

            await _actionPropertyDataProvider.AddDeploymentActionPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
        }
    }
}

