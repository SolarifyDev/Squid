using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.Project;
using Squid.Message.Domain.Deployments;
using ProjectEntity = Squid.Message.Domain.Deployments.Project;
using DeploymentProcessEntity = Squid.Message.Domain.Deployments.DeploymentProcess;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public class DeploymentPlanService : IDeploymentPlanService
{
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IHybridProcessSnapshotService _processSnapshotService;

    public DeploymentPlanService(
        IDeploymentDataProvider deploymentDataProvider,
        IProjectDataProvider projectDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IHybridProcessSnapshotService processSnapshotService)
    {
        _deploymentDataProvider = deploymentDataProvider;
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _processSnapshotService = processSnapshotService;
    }

    public async Task<DeploymentPlanDto> GeneratePlanAsync(int deploymentId)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId).ConfigureAwait(false);

        if (deployment == null)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _projectDataProvider.GetProjectByIdAsync(deployment.ProjectId).ConfigureAwait(false);

        if (project == null)
        {
            throw new InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }

        // 获取或创建流程快照
        ProcessSnapshotData processSnapshot;

        if (deployment.ProcessSnapshotId.HasValue)
        {
            // 如果已有快照ID，直接加载快照
            processSnapshot = await _processSnapshotService.LoadSnapshotAsync(deployment.ProcessSnapshotId.Value).ConfigureAwait(false);
        }
        else
        {
            // 如果没有快照ID，创建新快照
            var process = await _processDataProvider.GetDeploymentProcessByIdAsync(project.DeploymentProcessId).ConfigureAwait(false);

            if (process == null)
            {
                throw new InvalidOperationException($"DeploymentProcess {project.DeploymentProcessId} not found.");
            }

            var snapshotId = await _processSnapshotService.CreateSnapshotAsync(process.Id, "System").ConfigureAwait(false);
            processSnapshot = await _processSnapshotService.LoadSnapshotAsync(snapshotId).ConfigureAwait(false);

            // 更新Deployment记录快照ID
            deployment.ProcessSnapshotId = snapshotId;
            await _deploymentDataProvider.UpdateDeploymentAsync(deployment).ConfigureAwait(false);
        }

        var plan = new DeploymentPlanDto
        {
            DeploymentId = deploymentId,
            ProcessSnapshot = processSnapshot
        };

        return plan;
    }
}
