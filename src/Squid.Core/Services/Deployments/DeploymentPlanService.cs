using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;
using ProjectEntity = Squid.Message.Domain.Deployments.Project;
using DeploymentProcessEntity = Squid.Message.Domain.Deployments.DeploymentProcess;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public class DeploymentPlanService : IDeploymentPlanService
{
    private readonly SquidDbContext _dbContext;
    private readonly IDeploymentStepPlanner _stepPlanner;

    public DeploymentPlanService(SquidDbContext dbContext, IDeploymentStepPlanner stepPlanner)
    {
        _dbContext = dbContext;
        _stepPlanner = stepPlanner;
    }

    public async Task<DeploymentPlanDto> GeneratePlanAsync(int deploymentId)
    {
        var deployment = await _dbContext.Set<Deployment>().FindAsync(deploymentId);

        if (deployment == null)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _dbContext.Set<ProjectEntity>().FirstOrDefaultAsync(p => p.Id == deployment.ProjectId);

        if (project == null)
        {
            throw new InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }

        var process = await _dbContext.Set<DeploymentProcessEntity>()
            .FirstOrDefaultAsync(p => p.Id.ToString() == project.DeploymentProcessId.ToString());

        if (process == null)
        {
            throw new InvalidOperationException($"DeploymentProcess {project.DeploymentProcessId} not found.");
        }

        // 组装步骤/动作/属性快照
        var stepDtos = await _stepPlanner.PlanStepsAsync(deploymentId);

        var processes = new List<ProcessDetailSnapshotData>();
        var scopeDefinitions = new Dictionary<string, List<string>>();

        foreach (var step in stepDtos)
        {
            var stepProperties = new Dictionary<string, string>();
            foreach (var prop in step.Properties)
            {
                stepProperties[prop.PropertyName] = prop.PropertyValue;
            }

            var actions = new List<ActionSnapshotData>();
            foreach (var action in step.Actions)
            {
                var actionProperties = new Dictionary<string, string>();
                foreach (var ap in action.Properties)
                {
                    actionProperties[ap.PropertyName] = ap.PropertyValue;
                }

                actions.Add(new ActionSnapshotData
                {
                    Id = action.Id,
                    Name = action.Name,
                    ActionType = action.ActionType,
                    ActionOrder = action.StepId,
                    WorkerPoolId = null,
                    IsDisabled = action.IsDisabled,
                    IsRequired = true,
                    CanBeUsedForProjectVersioning = false,
                    CreatedAt = action.CreatedAt,
                    Properties = actionProperties,
                    Environments = new List<int>(),
                    Channels = new List<int>(),
                    MachineRoles = new List<string>()
                });
            }

            processes.Add(new ProcessDetailSnapshotData
            {
                Id = step.Id,
                Name = step.Name,
                StepType = step.StepType,
                StepOrder = step.StepOrder,
                Condition = step.Condition,
                Properties = stepProperties,
                CreatedAt = step.CreatedAt,
                Actions = actions
            });
        }

        // ScopeDefinitions 示例（可按实际业务补全）
        scopeDefinitions["Environment"] = new List<string>();
        scopeDefinitions["Role"] = new List<string>();

        var processSnapshot = new ProcessSnapshotData
        {
            Id = process.Id,
            Version = process.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            Processes = processes,
            ScopeDefinitions = scopeDefinitions
        };

        var plan = new DeploymentPlanDto
        {
            DeploymentId = deploymentId,
            ProcessSnapshot = processSnapshot
        };

        return plan;
    }
}
