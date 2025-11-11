using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Process;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public class DeploymentStepPlanner : IDeploymentStepPlanner
{
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentStepPropertyDataProvider _stepPropertyDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IActionEnvironmentDataProvider _actionEnvironmentDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;

    public DeploymentStepPlanner(
        IDeploymentDataProvider deploymentDataProvider,
        IProjectDataProvider projectDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IActionMachineRoleDataProvider actionMachineRoleDataProvider)
    {
        _deploymentDataProvider = deploymentDataProvider;
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _stepDataProvider = stepDataProvider;
        _actionDataProvider = actionDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
    }

    public async Task<List<DeploymentStepDto>> PlanStepsAsync(int deploymentId)
    {
        // 读取 Deployment、Project、DeploymentProcess、DeploymentStep、DeploymentAction
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId).ConfigureAwait(false);

        if (deployment == null)
        {
            throw new System.InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _projectDataProvider.GetProjectByIdAsync(deployment.ProjectId).ConfigureAwait(false);

        if (project == null)
        {
            throw new System.InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }

        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(project.DeploymentProcessId).ConfigureAwait(false);

        if (process == null)
        {
            throw new System.InvalidOperationException($"DeploymentProcess for Project {project.Id} not found.");
        }

        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(process.Id).ConfigureAwait(false);

        var stepIds = steps.Select(s => s.Id).ToList();

        var actions = await _actionDataProvider.GetDeploymentActionsByStepIdsAsync(stepIds).ConfigureAwait(false);

        var actionIds = actions.Select(a => a.Id).ToList();

        // 加载Step Properties
        var stepProperties = await _stepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdsAsync(stepIds).ConfigureAwait(false);

        // 加载Action Properties
        var actionProperties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdsAsync(actionIds).ConfigureAwait(false);

        // 加载目标筛选配置
        var actionEnvironments = await _actionEnvironmentDataProvider.GetActionEnvironmentsByActionIdsAsync(actionIds).ConfigureAwait(false);

        var actionChannels = await _actionChannelDataProvider.GetActionChannelsByActionIdsAsync(actionIds).ConfigureAwait(false);

        var actionMachineRoles = await _actionMachineRoleDataProvider.GetActionMachineRolesByActionIdsAsync(actionIds).ConfigureAwait(false);

        var stepDtos = steps.Select(step => new DeploymentStepDto
        {
            Id = step.Id,
            ProcessId = step.ProcessId,
            StepOrder = step.StepOrder,
            Name = step.Name,
            StepType = step.StepType,
            Condition = step.Condition,
            StartTrigger = step.StartTrigger,
            PackageRequirement = step.PackageRequirement,
            IsDisabled = step.IsDisabled,
            IsRequired = step.IsRequired,
            CreatedAt = step.CreatedAt,
            Properties = stepProperties.Where(sp => sp.StepId == step.Id)
                .Select(sp => new DeploymentStepPropertyDto
                {
                    Id = 0, // 复合主键实体没有单独的Id
                    StepId = sp.StepId,
                    PropertyName = sp.PropertyName,
                    PropertyValue = sp.PropertyValue
                }).ToList(),
            Actions = actions.Where(a => a.StepId == step.Id)
                .Select(a => new DeploymentActionDto
                {
                    Id = a.Id,
                    StepId = a.StepId,
                    Name = a.Name,
                    ActionType = a.ActionType,
                    IsDisabled = a.IsDisabled,
                    CreatedAt = a.CreatedAt,
                    Properties = actionProperties.Where(ap => ap.ActionId == a.Id)
                        .Select(ap => new DeploymentActionPropertyDto
                        {
                            Id = 0, // 复合主键实体没有单独的Id
                            ActionId = ap.ActionId,
                            PropertyName = ap.PropertyName,
                            PropertyValue = ap.PropertyValue
                        }).ToList(),
                    Environments = actionEnvironments.Where(ae => ae.ActionId == a.Id)
                        .Select(ae => ae.EnvironmentId).ToList(),
                    Channels = actionChannels.Where(ac => ac.ActionId == a.Id)
                        .Select(ac => ac.ChannelId).ToList(),
                    MachineRoles = actionMachineRoles.Where(amr => amr.ActionId == a.Id)
                        .Select(amr => amr.MachineRole).ToList()
                }).ToList()
        }).ToList();

        return stepDtos;
    }
}
