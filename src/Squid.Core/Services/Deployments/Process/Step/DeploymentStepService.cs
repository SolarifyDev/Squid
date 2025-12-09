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
    private readonly IActionEnvironmentDataProvider _actionEnvironmentDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;

    public DeploymentStepService(
        IMapper mapper,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IActionMachineRoleDataProvider actionMachineRoleDataProvider)
    {
        _mapper = mapper;
        _stepDataProvider = stepDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
    }

    public async Task<DeploymentStepCreatedEvent> CreateDeploymentStepAsync(CreateDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        var step = _mapper.Map<DeploymentStep>(command.Step);
        step.ProcessId = command.ProcessId;
        step.CreatedAt = DateTimeOffset.UtcNow;

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

        var stepDto = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);

        return new DeploymentStepCreatedEvent { Data = stepDto };
    }

    public async Task<DeploymentStepUpdatedEvent> UpdateDeploymentStepAsync(UpdateDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        var step = await _stepDataProvider.GetDeploymentStepByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (step == null)
        {
            throw new InvalidOperationException($"DeploymentStep with id {command.Id} not found");
        }

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

        var stepDto = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);

        return new DeploymentStepUpdatedEvent { Data = stepDto };
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
        var stepDto = await GetStepWithRelatedDataAsync(id, cancellationToken).ConfigureAwait(false);
        if (stepDto == null)
        {
            throw new InvalidOperationException($"DeploymentStep with id {id} not found");
        }

        return new GetDeploymentStepResponse { Data = stepDto };
    }

    public async Task<GetDeploymentStepsResponse> GetDeploymentStepsAsync(GetDeploymentStepsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _stepDataProvider.GetDeploymentStepPagingAsync(
            request.ProcessId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        var stepDtos = new List<DeploymentStepDto>();

        foreach (var step in data)
        {
            var stepDto = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);
            stepDtos.Add(stepDto);
        }

        return new GetDeploymentStepsResponse
        {
            Data = new GetDeploymentStepsResponseData { Count = count, Steps = stepDtos }
        };
    }

    private async Task<DeploymentStepDto> GetStepWithRelatedDataAsync(int stepId, CancellationToken cancellationToken)
    {
        var step = await _stepDataProvider.GetDeploymentStepByIdAsync(stepId, cancellationToken).ConfigureAwait(false);
        if (step == null) return null;

        var stepDto = _mapper.Map<DeploymentStepDto>(step);

        var stepProperties = await _stepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);
        stepDto.Properties = _mapper.Map<List<DeploymentStepPropertyDto>>(stepProperties);

        var actions = await _actionDataProvider.GetDeploymentActionsByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);
        stepDto.Actions = new List<DeploymentActionDto>();

        foreach (var action in actions)
        {
            var actionDto = await MapActionWithRelatedDataAsync(action, cancellationToken).ConfigureAwait(false);
            stepDto.Actions.Add(actionDto);
        }

        return stepDto;
    }

    private async Task<DeploymentActionDto> MapActionWithRelatedDataAsync(DeploymentAction action, CancellationToken cancellationToken)
    {
        var actionDto = _mapper.Map<DeploymentActionDto>(action);

        var properties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Properties = _mapper.Map<List<DeploymentActionPropertyDto>>(properties);

        return actionDto;
    }

    private async Task CreateActionsAsync(int stepId, List<DeploymentActionDto> actionDtos, CancellationToken cancellationToken)
    {
        var actions = new List<DeploymentAction>();
        var allProperties = new List<DeploymentActionProperty>();

        foreach (var actionDto in actionDtos)
        {
            var action = _mapper.Map<DeploymentAction>(actionDto);
            action.StepId = stepId;
            action.CreatedAt = DateTimeOffset.UtcNow;
            actions.Add(action);

            if (actionDto.Properties?.Any() == true)
            {
                var properties = _mapper.Map<List<DeploymentActionProperty>>(actionDto.Properties);
                properties.ForEach(p => p.ActionId = action.Id);
                allProperties.AddRange(properties);
            }
        }

        await _actionDataProvider.AddDeploymentActionsAsync(actions, false, cancellationToken).ConfigureAwait(false);

        await _actionPropertyDataProvider.AddDeploymentActionPropertiesAsync(allProperties, cancellationToken).ConfigureAwait(false);
    }
}

