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

    Task<GetDeploymentStepsResponse> GetDeploymentStepsAsync(GetDeploymentStepsRequest request, CancellationToken cancellationToken);

    Task<GetDeploymentStepResponse> GetDeploymentStepAsync(
        GetDeploymentStepRequest request,
        CancellationToken cancellationToken);
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
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;
    private readonly IProcessContextGuard _processContextGuard;

    public DeploymentStepService(
        IMapper mapper,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IActionExcludedEnvironmentDataProvider actionExcludedEnvironmentDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IActionMachineRoleDataProvider actionMachineRoleDataProvider,
        IProcessContextGuard processContextGuard)
    {
        _mapper = mapper;
        _stepDataProvider = stepDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionExcludedEnvironmentDataProvider = actionExcludedEnvironmentDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
        _processContextGuard = processContextGuard;
    }

    public async Task<DeploymentStepCreatedEvent> CreateDeploymentStepAsync(CreateDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        await ResolveWriteContextAsync(
            command.ProcessId,
            command.ProjectId,
            command.RunbookId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);

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

        await ResolveWriteContextAsync(
            step.ProcessId,
            command.ProjectId,
            command.RunbookId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);

        await ValidateSubmittedActionIdsAsync(
            step.Id,
            command.Step.Actions,
            cancellationToken).ConfigureAwait(false);

        _mapper.Map(command.Step, step);

        await _stepDataProvider.UpdateDeploymentStepAsync(step, true, cancellationToken).ConfigureAwait(false);

        await _stepPropertyDataProvider.DeleteDeploymentStepPropertiesByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);

        if (command.Step.Properties?.Any() == true)
        {
            var properties = _mapper.Map<List<DeploymentStepProperty>>(command.Step.Properties);
            
            properties.ForEach(p => p.StepId = step.Id);

            await _stepPropertyDataProvider.AddDeploymentStepPropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
        }

        await UpsertActionsAsync(step.Id, command.Step.Actions, cancellationToken).ConfigureAwait(false);

        var stepWithRelatedData = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);

        return new DeploymentStepUpdatedEvent { Data = stepWithRelatedData };
    }

    public async Task<DeploymentStepDeletedEvent> DeleteDeploymentStepsAsync(DeleteDeploymentStepCommand command, CancellationToken cancellationToken)
    {
        if (command.Ids == null
            || command.Ids.Count == 0
            || command.Ids.Any(id => id <= 0)
            || command.Ids.Distinct().Count() != command.Ids.Count)
        {
            throw new ProcessContextMismatchException(
                "Every submitted deployment step ID must be positive and unique.");
        }

        var steps = await _stepDataProvider.GetDeploymentStepsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        var resolvedSteps = await ResolveSubmittedStepsAsync(
            command.Ids,
            steps,
            null,
            command.ProjectId,
            command.RunbookId,
            command.SpaceId,
            write: true,
            cancellationToken).ConfigureAwait(false);

        steps = resolvedSteps;
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
        await ResolveWriteContextAsync(
            command.ProcessId,
            command.ProjectId,
            command.RunbookId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);

        var existingSteps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(command.ProcessId, cancellationToken).ConfigureAwait(false);
        var distinctOrderStepIds = command.StepOrders.Select(o => o.StepId).Distinct().Count();

        if (distinctOrderStepIds != command.StepOrders.Count)
        {
            throw new ProcessContextMismatchException(
                "Every submitted deployment step must be unique.");
        }

        await ResolveSubmittedStepsAsync(
            command.StepOrders.Select(o => o.StepId).ToList(),
            existingSteps,
            command.ProcessId,
            command.ProjectId,
            command.RunbookId,
            command.SpaceId,
            write: true,
            cancellationToken).ConfigureAwait(false);

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

    public async Task<GetDeploymentStepsResponse> GetDeploymentStepsAsync(GetDeploymentStepsRequest request, CancellationToken cancellationToken)
    {
        await ResolveReadContextAsync(
            request.ProcessId,
            request.ProjectId,
            request.RunbookId,
            request.SpaceId,
            cancellationToken).ConfigureAwait(false);

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

    public async Task<GetDeploymentStepResponse> GetDeploymentStepAsync(
        GetDeploymentStepRequest request,
        CancellationToken cancellationToken)
    {
        var step = await _stepDataProvider.GetDeploymentStepByIdAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (step == null)
        {
            throw new DeploymentEntityNotFoundException("DeploymentStep", request.Id);
        }

        await ResolveReadContextAsync(
            request.ProcessId ?? step.ProcessId,
            request.ProjectId,
            request.RunbookId,
            request.SpaceId,
            cancellationToken,
            expectedStep: step,
            explicitProcessId: request.ProcessId.HasValue).ConfigureAwait(false);

        var stepWithRelatedData = await GetStepWithRelatedDataAsync(step.Id, cancellationToken).ConfigureAwait(false);
        return new GetDeploymentStepResponse { Data = stepWithRelatedData };
    }

    private async Task<ProcessContext> ResolveReadContextAsync(
        int processId,
        int? projectId,
        int? runbookId,
        int? spaceId,
        CancellationToken cancellationToken,
        DeploymentStep expectedStep = null,
        bool explicitProcessId = true)
    {
        if (!explicitProcessId && processId <= 0)
        {
            throw new ProcessContextMismatchException(
                "ProcessId context is required when a step has no persisted process.");
        }

        var context = await _processContextGuard.ResolveForReadAsync(
            new ProcessContextRequest(processId, spaceId, projectId, runbookId),
            cancellationToken).ConfigureAwait(false);

        if (expectedStep != null && expectedStep.ProcessId != context.ProcessId)
        {
            throw new ProcessContextMismatchException(
                $"DeploymentStep {expectedStep.Id} does not belong to process {context.ProcessId}.");
        }

        return context;
    }

    private async Task<ProcessContext> ResolveWriteContextAsync(
        int processId,
        int? projectId,
        int? runbookId,
        int? spaceId,
        CancellationToken cancellationToken)
    {
        return await _processContextGuard.ResolveForWriteAsync(
            new ProcessContextRequest(processId, spaceId, projectId, runbookId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<DeploymentStep>> ResolveSubmittedStepsAsync(
        IReadOnlyCollection<int> submittedStepIds,
        List<DeploymentStep> persistedSteps,
        int? expectedProcessId,
        int? projectId,
        int? runbookId,
        int? spaceId,
        bool write,
        CancellationToken cancellationToken)
    {
        var distinctStepIds = submittedStepIds?.Where(id => id > 0).Distinct().ToList()
            ?? new List<int>();
        var distinctPersistedSteps = persistedSteps
            .Where(step => distinctStepIds.Contains(step.Id))
            .ToList();

        if (distinctStepIds.Count == 0 || distinctPersistedSteps.Count != distinctStepIds.Count)
        {
            throw new ProcessContextMismatchException(
                "Every submitted deployment step must exist in the resolved process.");
        }

        var processIds = distinctPersistedSteps.Select(step => step.ProcessId).Distinct().ToList();
        if (processIds.Count != 1)
        {
            throw new ProcessContextMismatchException(
                "Every submitted deployment step must belong to the same process.");
        }

        var resolvedProcessId = processIds[0];
        if (expectedProcessId.HasValue && resolvedProcessId != expectedProcessId.Value)
        {
            throw new ProcessContextMismatchException(
                $"Submitted deployment steps do not belong to process {expectedProcessId.Value}.");
        }

        var request = new ProcessContextRequest(resolvedProcessId, spaceId, projectId, runbookId);
        if (write)
        {
            await _processContextGuard.ResolveForWriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _processContextGuard.ResolveForReadAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return distinctPersistedSteps;
    }

    private async Task ValidateSubmittedActionIdsAsync(
        int stepId,
        List<CreateOrUpdateDeploymentActionModel> actions,
        CancellationToken cancellationToken)
    {
        if (actions == null || actions.Count == 0)
        {
            return;
        }

        var submittedActionIds = actions
            .Where(action => action.Id is > 0)
            .Select(action => action.Id.Value)
            .ToList();

        if (submittedActionIds.Count != submittedActionIds.Distinct().Count())
        {
            throw new ProcessContextMismatchException(
                "A deployment action cannot be submitted more than once.");
        }

        if (submittedActionIds.Count == 0)
        {
            return;
        }

        var allBelongToStep = await _actionDataProvider
            .AllBelongToStepAsync(submittedActionIds, stepId, cancellationToken)
            .ConfigureAwait(false);

        if (!allBelongToStep)
        {
            throw new ProcessContextMismatchException(
                "Every submitted deployment action must belong to the resolved deployment step.");
        }
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
        for (var i = 0; i < actions.Count; i++)
        {
            await CreateActionAsync(stepId, actions[i], i + 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpsertActionsAsync(int stepId, List<CreateOrUpdateDeploymentActionModel> actions, CancellationToken cancellationToken)
    {
        actions ??= new List<CreateOrUpdateDeploymentActionModel>();

        var existingActions = await _actionDataProvider.GetDeploymentActionsByStepIdAsync(stepId, cancellationToken).ConfigureAwait(false);
        var existingActionsById = existingActions.ToDictionary(a => a.Id);
        var hasExplicitActionIds = actions.Any(a => a.Id is > 0);
        var retainedActionIds = new HashSet<int>();

        if (actions.Count > 0)
        {
            await AssignTemporaryActionOrdersAsync(existingActions, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var existingAction = ResolveExistingAction(stepId, action, existingActions, existingActionsById, retainedActionIds, i, hasExplicitActionIds);

            if (existingAction == null)
            {
                await CreateActionAsync(stepId, action, i + 1, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _mapper.Map(action, existingAction);
            existingAction.StepId = stepId;
            existingAction.ActionOrder = i + 1;

            await _actionDataProvider.UpdateDeploymentActionAsync(existingAction, true, cancellationToken).ConfigureAwait(false);
            await ReplaceActionRelatedDataAsync(existingAction.Id, action, cancellationToken).ConfigureAwait(false);
        }

        var removedActions = existingActions.Where(a => !retainedActionIds.Contains(a.Id)).ToList();

        foreach (var removedAction in removedActions)
        {
            await _actionDataProvider.DeleteDeploymentActionAsync(removedAction, true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AssignTemporaryActionOrdersAsync(List<DeploymentAction> existingActions, CancellationToken cancellationToken)
    {
        for (var i = 0; i < existingActions.Count; i++)
        {
            existingActions[i].ActionOrder = -(i + 1);
            var isLast = i == existingActions.Count - 1;
            await _actionDataProvider.UpdateDeploymentActionAsync(existingActions[i], isLast, cancellationToken).ConfigureAwait(false);
        }
    }

    private static DeploymentAction ResolveExistingAction(
        int stepId,
        CreateOrUpdateDeploymentActionModel action,
        List<DeploymentAction> existingActions,
        Dictionary<int, DeploymentAction> existingActionsById,
        HashSet<int> retainedActionIds,
        int actionIndex,
        bool hasExplicitActionIds)
    {
        if (action.Id is > 0)
        {
            if (!existingActionsById.TryGetValue(action.Id.Value, out var existingAction))
                throw new InvalidOperationException($"DeploymentAction {action.Id.Value} does not belong to DeploymentStep {stepId}.");

            if (!retainedActionIds.Add(existingAction.Id))
                throw new InvalidOperationException($"DeploymentAction {action.Id.Value} was submitted more than once.");

            return existingAction;
        }

        if (hasExplicitActionIds || actionIndex >= existingActions.Count) return null;

        var fallbackAction = existingActions[actionIndex];

        if (!retainedActionIds.Add(fallbackAction.Id)) return null;

        return fallbackAction;
    }

    private async Task ReplaceActionRelatedDataAsync(int actionId, CreateOrUpdateDeploymentActionModel action, CancellationToken cancellationToken)
    {
        var properties = _mapper.Map<List<DeploymentActionProperty>>(action.Properties ?? new List<ActionPropertyModel>());
        properties.ForEach(p => p.ActionId = actionId);
        await _actionPropertyDataProvider.UpdateDeploymentActionPropertiesAsync(actionId, properties, cancellationToken).ConfigureAwait(false);

        await _actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await PersistActionEnvironmentsAsync(actionId, action.Environments, cancellationToken).ConfigureAwait(false);

        await _actionExcludedEnvironmentDataProvider.DeleteActionExcludedEnvironmentsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await PersistActionExcludedEnvironmentsAsync(actionId, action.ExcludedEnvironments, cancellationToken).ConfigureAwait(false);

        await _actionChannelDataProvider.DeleteActionChannelsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await PersistActionChannelsAsync(actionId, action.Channels, cancellationToken).ConfigureAwait(false);

        await _actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateActionAsync(int stepId, CreateOrUpdateDeploymentActionModel action, int actionOrder, CancellationToken cancellationToken)
    {
        var mappedAction = _mapper.Map<DeploymentAction>(action);

        mappedAction.StepId = stepId;
        mappedAction.ActionOrder = actionOrder;

        await _actionDataProvider.AddDeploymentActionAsync(mappedAction, true, cancellationToken).ConfigureAwait(false);

        await PersistActionPropertiesAsync(mappedAction.Id, action.Properties, cancellationToken).ConfigureAwait(false);
        await PersistActionEnvironmentsAsync(mappedAction.Id, action.Environments, cancellationToken).ConfigureAwait(false);
        await PersistActionExcludedEnvironmentsAsync(mappedAction.Id, action.ExcludedEnvironments, cancellationToken).ConfigureAwait(false);
        await PersistActionChannelsAsync(mappedAction.Id, action.Channels, cancellationToken).ConfigureAwait(false);
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
