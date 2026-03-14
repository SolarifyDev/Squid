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

    Task<DeploymentStepsReorderedEvent> ReorderDeploymentStepsAsync(ReorderDeploymentStepsCommand command, CancellationToken cancellationToken);

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
    private readonly IActionExcludedEnvironmentDataProvider _actionExcludedEnvironmentDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;

    public DeploymentStepService(
        IMapper mapper,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IActionExcludedEnvironmentDataProvider actionExcludedEnvironmentDataProvider,
        IActionChannelDataProvider actionChannelDataProvider)
    {
        _mapper = mapper;
        _stepDataProvider = stepDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionExcludedEnvironmentDataProvider = actionExcludedEnvironmentDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
    }

    public async Task<DeploymentStepCreatedEvent> CreateDeploymentStepAsync(CreateDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        var step = _mapper.Map<DeploymentStep>(command.Step);
        
        step.ProcessId = command.ProcessId;
        step.StepOrder = await ResolveNextStepOrderAsync(command.ProcessId, cancellationToken).ConfigureAwait(false);

        await _stepDataProvider.AddDeploymentStepAsync(step, true, cancellationToken).ConfigureAwait(false);

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

        await _stepDataProvider.UpdateDeploymentStepAsync(step, true, cancellationToken).ConfigureAwait(false);

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
        var processIds = steps.Select(s => s.ProcessId).Distinct().ToList();

        await _stepDataProvider.DeleteDeploymentStepsAsync(steps, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var processId in processIds)
            await ReindexStepOrdersAsync(processId, cancellationToken).ConfigureAwait(false);

        return new DeploymentStepDeletedEvent
        {
            Data = new DeleteDeploymentStepResponseData
            {
                FailIds = command.Ids.Except(steps.Select(s => s.Id)).ToList()
            }
        };
    }

    public async Task<DeploymentStepsReorderedEvent> ReorderDeploymentStepsAsync(ReorderDeploymentStepsCommand command, CancellationToken cancellationToken)
    {
        var existingSteps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(command.ProcessId, cancellationToken).ConfigureAwait(false);

        var existingStepIds = existingSteps.Select(s => s.Id).ToHashSet();
        var submittedStepIds = command.StepOrders.Select(o => o.StepId).ToHashSet();

        if (!submittedStepIds.SetEquals(existingStepIds))
            throw new InvalidOperationException($"StepOrders must include all steps for process {command.ProcessId}. Expected: [{string.Join(", ", existingStepIds)}], Got: [{string.Join(", ", submittedStepIds)}]");

        // Normalize client-provided ordering to contiguous 1-based values
        var sortedOrders = command.StepOrders.OrderBy(o => o.StepOrder).ToList();
        var orderLookup = new Dictionary<int, int>();

        for (var i = 0; i < sortedOrders.Count; i++)
            orderLookup[sortedOrders[i].StepId] = i + 1;

        // Phase 1: Set temporary negative orders to avoid unique constraint violations on (process_id, step_order)
        for (var i = 0; i < existingSteps.Count; i++)
        {
            existingSteps[i].StepOrder = -(i + 1);
            var isLast = i == existingSteps.Count - 1;
            await _stepDataProvider.UpdateDeploymentStepAsync(existingSteps[i], isLast, cancellationToken).ConfigureAwait(false);
        }

        // Phase 2: Set real orders — all steps now have negative values, so no conflicts
        for (var i = 0; i < existingSteps.Count; i++)
        {
            existingSteps[i].StepOrder = orderLookup[existingSteps[i].Id];
            var isLast = i == existingSteps.Count - 1;
            await _stepDataProvider.UpdateDeploymentStepAsync(existingSteps[i], isLast, cancellationToken).ConfigureAwait(false);
        }

        var reorderedSteps = new List<DeploymentStepDto>();

        foreach (var step in existingSteps.OrderBy(s => s.StepOrder))
        {
            var stepDto = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);
            reorderedSteps.Add(stepDto);
        }

        return new DeploymentStepsReorderedEvent { Data = reorderedSteps };
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

        var environments = await _actionEnvironmentDataProvider.GetActionEnvironmentsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Environments = environments.Select(e => e.EnvironmentId).ToList();

        var excludedEnvironments = await _actionExcludedEnvironmentDataProvider.GetActionExcludedEnvironmentsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.ExcludedEnvironments = excludedEnvironments.Select(e => e.EnvironmentId).ToList();

        var channels = await _actionChannelDataProvider.GetActionChannelsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Channels = channels.Select(c => c.ChannelId).ToList();

        return actionDto;
    }

    private async Task ReindexStepOrdersAsync(int processId, CancellationToken cancellationToken)
    {
        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(processId, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < steps.Count; i++)
        {
            var expectedOrder = i + 1;

            if (steps[i].StepOrder == expectedOrder) continue;

            steps[i].StepOrder = -(i + 1);
            await _stepDataProvider.UpdateDeploymentStepAsync(steps[i], false, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var expectedOrder = i + 1;

            if (steps[i].StepOrder == expectedOrder) continue;

            steps[i].StepOrder = expectedOrder;
            var isLast = i == steps.Count - 1;
            await _stepDataProvider.UpdateDeploymentStepAsync(steps[i], isLast, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> ResolveNextStepOrderAsync(int processId, CancellationToken cancellationToken)
    {
        var existingSteps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(processId, cancellationToken).ConfigureAwait(false);

        return existingSteps.Count == 0 ? 1 : existingSteps.Max(s => s.StepOrder) + 1;
    }

    private async Task CreateActionsAsync(int stepId, List<CreateOrUpdateDeploymentActionModel> actions, CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            var mappedAction = _mapper.Map<DeploymentAction>(action);

            mappedAction.StepId = stepId;

            await _actionDataProvider.AddDeploymentActionAsync(mappedAction, true, cancellationToken).ConfigureAwait(false);

            await PersistActionPropertiesAsync(mappedAction.Id, action.Properties, cancellationToken).ConfigureAwait(false);
            await PersistActionEnvironmentsAsync(mappedAction.Id, action.Environments, cancellationToken).ConfigureAwait(false);
            await PersistActionExcludedEnvironmentsAsync(mappedAction.Id, action.ExcludedEnvironments, cancellationToken).ConfigureAwait(false);
            await PersistActionChannelsAsync(mappedAction.Id, action.Channels, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistActionPropertiesAsync(int actionId, List<ActionPropertyModel> properties, CancellationToken cancellationToken)
    {
        if (properties?.Any() != true) return;

        var mapped = _mapper.Map<List<DeploymentActionProperty>>(properties);
        mapped.ForEach(p => p.ActionId = actionId);

        await _actionPropertyDataProvider.AddDeploymentActionPropertiesAsync(mapped, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistActionEnvironmentsAsync(int actionId, List<int> environmentIds, CancellationToken cancellationToken)
    {
        if (environmentIds == null || environmentIds.Count == 0) return;

        var entities = environmentIds.Select(id => new ActionEnvironment { ActionId = actionId, EnvironmentId = id }).ToList();

        await _actionEnvironmentDataProvider.AddActionEnvironmentsAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistActionExcludedEnvironmentsAsync(int actionId, List<int> environmentIds, CancellationToken cancellationToken)
    {
        if (environmentIds == null || environmentIds.Count == 0) return;

        var entities = environmentIds.Select(id => new ActionExcludedEnvironment { ActionId = actionId, EnvironmentId = id }).ToList();

        await _actionExcludedEnvironmentDataProvider.AddActionExcludedEnvironmentsAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistActionChannelsAsync(int actionId, List<int> channelIds, CancellationToken cancellationToken)
    {
        if (channelIds == null || channelIds.Count == 0) return;

        var entities = channelIds.Select(id => new ActionChannel { ActionId = actionId, ChannelId = id }).ToList();

        await _actionChannelDataProvider.AddActionChannelsAsync(entities, cancellationToken).ConfigureAwait(false);
    }
}

