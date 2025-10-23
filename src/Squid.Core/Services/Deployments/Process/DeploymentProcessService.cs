using AutoMapper;
using Squid.Core.DependencyInjection;
using Squid.Message.Commands.Deployments.Process;
using Squid.Message.Domain.Deployments;
using Squid.Message.Events.Deployments.Process;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Requests.Deployments.Process;

namespace Squid.Core.Services.Deployments.Process;

public interface IDeploymentProcessService : IScopedDependency
{
    Task<DeploymentProcessCreatedEvent> CreateDeploymentProcessAsync(CreateDeploymentProcessCommand command, CancellationToken cancellationToken);
    
    Task<DeploymentProcessUpdatedEvent> UpdateDeploymentProcessAsync(UpdateDeploymentProcessCommand command, CancellationToken cancellationToken);
    
    Task DeleteDeploymentProcessAsync(Guid id, CancellationToken cancellationToken);
    
    Task<DeploymentProcessDto> GetDeploymentProcessByIdAsync(Guid id, CancellationToken cancellationToken);
    
    Task<GetDeploymentProcessesResponse> GetDeploymentProcessesAsync(GetDeploymentProcessesRequest request, CancellationToken cancellationToken);
}

public class DeploymentProcessService : IDeploymentProcessService
{
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentStepPropertyDataProvider _stepPropertyDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IActionEnvironmentDataProvider _actionEnvironmentDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IActionTenantTagDataProvider _actionTenantTagDataProvider;
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;
    private readonly IMapper _mapper;

    public DeploymentProcessService(
        IMapper mapper,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IActionTenantTagDataProvider actionTenantTagDataProvider,
        IActionMachineRoleDataProvider actionMachineRoleDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider)
    {
        _mapper = mapper;
        _stepDataProvider = stepDataProvider;
        _actionDataProvider = actionDataProvider;
        _processDataProvider = processDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionTenantTagDataProvider = actionTenantTagDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
    }

    public async Task<DeploymentProcessCreatedEvent> CreateDeploymentProcessAsync(CreateDeploymentProcessCommand command, CancellationToken cancellationToken)
    {
        var process = _mapper.Map<DeploymentProcess>(command);
        process.Id = Guid.NewGuid();
        process.Version = await _processDataProvider.GetNextVersionAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        process.CreatedAt = DateTimeOffset.Now;
        process.LastModified = DateTimeOffset.Now;

        await _processDataProvider.AddDeploymentProcessAsync(process, cancellationToken: cancellationToken).ConfigureAwait(false);

        await CreateStepsAsync(process.Id, command.Steps, cancellationToken).ConfigureAwait(false);

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
        process.LastModified = DateTimeOffset.Now;

        await _processDataProvider.UpdateDeploymentProcessAsync(process, cancellationToken: cancellationToken).ConfigureAwait(false);

        await _stepDataProvider.DeleteDeploymentStepsByProcessIdAsync(process.Id, cancellationToken).ConfigureAwait(false);
        await CreateStepsAsync(process.Id, command.Steps, cancellationToken).ConfigureAwait(false);

        return new DeploymentProcessUpdatedEvent
        {
            DeploymentProcess = _mapper.Map<DeploymentProcessDto>(process)
        };
    }

    public async Task DeleteDeploymentProcessAsync(Guid id, CancellationToken cancellationToken)
    {
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (process == null)
        {
            throw new InvalidOperationException($"DeploymentProcess with id {id} not found");
        }

        await _stepDataProvider.DeleteDeploymentStepsByProcessIdAsync(id, cancellationToken).ConfigureAwait(false);
        await _processDataProvider.DeleteDeploymentProcessAsync(process, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentProcessDto> GetDeploymentProcessByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (process == null)
        {
            throw new InvalidOperationException($"DeploymentProcess with id {id} not found");
        }

        var processDto = _mapper.Map<DeploymentProcessDto>(process);

        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(id, cancellationToken).ConfigureAwait(false);
        processDto.Steps = new List<DeploymentStepDto>();

        foreach (var step in steps)
        {
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

            processDto.Steps.Add(stepDto);
        }

        return processDto;
    }

    public async Task<GetDeploymentProcessesResponse> GetDeploymentProcessesAsync(GetDeploymentProcessesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _processDataProvider.GetDeploymentProcessPagingAsync(
            request.ProjectId, request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetDeploymentProcessesResponse
        {
            Data = new GetDeploymentProcessesResponseData
            {
                Count = count,
                DeploymentProcesses = _mapper.Map<List<DeploymentProcessDto>>(data)
            }
        };
    }

    private async Task CreateStepsAsync(Guid processId, List<DeploymentStepDto> stepDtos, CancellationToken cancellationToken)
    {
        if (!stepDtos.Any()) return;

        var steps = new List<DeploymentStep>();
        var allStepProperties = new List<DeploymentStepProperty>();
        var allActionData = new BatchActionData();

        foreach (var stepDto in stepDtos)
        {
            var step = _mapper.Map<DeploymentStep>(stepDto);
            step.Id = Guid.NewGuid();
            step.ProcessId = processId;
            step.CreatedAt = DateTimeOffset.Now;
            steps.Add(step);

            if (stepDto.Properties?.Any() == true)
            {
                var properties = _mapper.Map<List<DeploymentStepProperty>>(stepDto.Properties);
                properties.ForEach(p => p.StepId = step.Id);
                allStepProperties.AddRange(properties);
            }

            if (stepDto.Actions?.Any() == true)
            {
                CollectActionsData(step.Id, stepDto.Actions, allActionData);
            }
        }

        await _stepDataProvider.AddDeploymentStepsAsync(steps, false, cancellationToken).ConfigureAwait(false);

        await _stepPropertyDataProvider.AddDeploymentStepPropertiesAsync(allStepProperties, cancellationToken).ConfigureAwait(false);

        await CreateBatchActionsAsync(allActionData, cancellationToken).ConfigureAwait(false);
    }
    
    private async Task<DeploymentActionDto> MapActionWithRelatedDataAsync(DeploymentAction action, CancellationToken cancellationToken)
    {
        var actionDto = _mapper.Map<DeploymentActionDto>(action);

        var properties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Properties = _mapper.Map<List<DeploymentActionPropertyDto>>(properties);

        var environments = await _actionEnvironmentDataProvider.GetActionEnvironmentsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Environments = environments.Select(e => e.EnvironmentId).ToList();

        var channels = await _actionChannelDataProvider.GetActionChannelsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.Channels = channels.Select(c => c.ChannelId).ToList();

        var tenantTags = await _actionTenantTagDataProvider.GetActionTenantTagsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.TenantTags = tenantTags.Select(t => t.TenantTag).ToList(); 

        var machineRoles = await _actionMachineRoleDataProvider.GetActionMachineRolesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        actionDto.MachineRoles = machineRoles.Select(m => m.MachineRole).ToList();

        return actionDto;
    }

    private class BatchActionData
    {
        public List<DeploymentAction> Actions { get; set; } = new List<DeploymentAction>();
        public List<DeploymentActionProperty> ActionProperties { get; set; } = new List<DeploymentActionProperty>();
        public List<ActionEnvironment> Environments { get; set; } = new List<ActionEnvironment>();
        public List<ActionChannel> Channels { get; set; } = new List<ActionChannel>();
        public List<ActionTenantTag> TenantTags { get; set; } = new List<ActionTenantTag>();
        public List<ActionMachineRole> MachineRoles { get; set; } = new List<ActionMachineRole>();
    }

    private void CollectActionsData(Guid stepId, List<DeploymentActionDto> actionDtos, BatchActionData batchData)
    {
        foreach (var actionDto in actionDtos)
        {
            var action = _mapper.Map<DeploymentAction>(actionDto);
            action.Id = Guid.NewGuid();
            action.StepId = stepId;
            action.CreatedAt = DateTimeOffset.Now;
            batchData.Actions.Add(action);

            if (actionDto.Properties?.Any() == true)
            {
                var properties = _mapper.Map<List<DeploymentActionProperty>>(actionDto.Properties);
                properties.ForEach(p => p.ActionId = action.Id);
                batchData.ActionProperties.AddRange(properties);
            }

            if (actionDto.Environments?.Any() == true)
            {
                var environments = actionDto.Environments.Select(e => new ActionEnvironment { ActionId = action.Id, EnvironmentId = e }).ToList();
                batchData.Environments.AddRange(environments);
            }

            if (actionDto.Channels?.Any() == true)
            {
                var channels = actionDto.Channels.Select(c => new ActionChannel { ActionId = action.Id, ChannelId = c }).ToList();
                batchData.Channels.AddRange(channels);
            }

            if (actionDto.TenantTags?.Any() == true)
            {
                var tenantTags = actionDto.TenantTags.Select(t => new ActionTenantTag { ActionId = action.Id, TenantTag = t }).ToList();
                batchData.TenantTags.AddRange(tenantTags);
            }

            if (actionDto.MachineRoles?.Any() == true)
            {
                var machineRoles = actionDto.MachineRoles.Select(m => new ActionMachineRole { ActionId = action.Id, MachineRole = m }).ToList();
                batchData.MachineRoles.AddRange(machineRoles);
            }
        }
    }

    private async Task CreateBatchActionsAsync(BatchActionData batchData, CancellationToken cancellationToken)
    {
        await _actionDataProvider.AddDeploymentActionsAsync(batchData.Actions, false, cancellationToken).ConfigureAwait(false);

        await _actionPropertyDataProvider.AddDeploymentActionPropertiesAsync(batchData.ActionProperties, cancellationToken).ConfigureAwait(false);

        await _actionEnvironmentDataProvider.AddActionEnvironmentsAsync(batchData.Environments, cancellationToken).ConfigureAwait(false);

        await _actionChannelDataProvider.AddActionChannelsAsync(batchData.Channels, cancellationToken).ConfigureAwait(false);

        await _actionTenantTagDataProvider.AddActionTenantTagsAsync(batchData.TenantTags, cancellationToken).ConfigureAwait(false);

        await _actionMachineRoleDataProvider.AddActionMachineRolesAsync(batchData.MachineRoles, cancellationToken).ConfigureAwait(false);
    }
}
