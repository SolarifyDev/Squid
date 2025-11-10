using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public class DeploymentStepPlanner : IDeploymentStepPlanner
{
    private readonly SquidDbContext _dbContext;

    public DeploymentStepPlanner(SquidDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<DeploymentStepDto>> PlanStepsAsync(int deploymentId)
    {
        // 读取 Deployment、Project、DeploymentProcess、DeploymentStep、DeploymentAction
        var deployment = await _dbContext.Set<Squid.Message.Domain.Deployments.Deployment>().FindAsync(deploymentId);

        if (deployment == null)
        {
            throw new System.InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _dbContext.Set<Squid.Message.Domain.Deployments.Project>().FirstOrDefaultAsync(p => p.Id == deployment.ProjectId);

        if (project == null)
        {
            throw new System.InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }

        var process = await _dbContext.Set<Squid.Message.Domain.Deployments.DeploymentProcess>().FirstOrDefaultAsync(p => p.ProjectId == project.Id);

        if (process == null)
        {
            throw new System.InvalidOperationException($"DeploymentProcess for Project {project.Id} not found.");
        }

        var steps = await _dbContext.Set<Squid.Message.Domain.Deployments.DeploymentStep>()
            .Where(s => s.ProcessId == process.Id)
            .OrderBy(s => s.StepOrder)
            .ToListAsync();

        var actions = await _dbContext.Set<Squid.Message.Domain.Deployments.DeploymentAction>()
            .Where(a => steps.Select(s => s.Id).Contains(a.StepId))
            .ToListAsync();

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
            Properties = new List<DeploymentStepPropertyDto>(), // 可扩展
            Actions = actions.Where(a => a.StepId == step.Id)
                .Select(a => new DeploymentActionDto
                {
                    Id = a.Id,
                    StepId = a.StepId,
                    Name = a.Name,
                    ActionType = a.ActionType,
                    IsDisabled = a.IsDisabled,
                    CreatedAt = a.CreatedAt,
                    Properties = new List<DeploymentActionPropertyDto>() // 可扩展
                }).ToList()
        }).ToList();

        return stepDtos;
    }
}
