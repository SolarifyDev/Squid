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

    public async Task<DeploymentPlanDto> GeneratePlanAsync(int deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId, cancellationToken).ConfigureAwait(false);

        if (deployment == null)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _projectDataProvider.GetProjectByIdAsync(deployment.ProjectId, cancellationToken).ConfigureAwait(false);

        if (project == null)
        {
            throw new InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }
        
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(project.DeploymentProcessId, cancellationToken).ConfigureAwait(false);

        // 获取或创建流程快照
        ProcessSnapshotData processSnapshot;
        
        if (deployment.ProcessSnapshotId.HasValue)
        {
            processSnapshot = await _processSnapshotService.LoadSnapshotAsync(deployment.ProcessSnapshotId.Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            processSnapshot = await _processSnapshotService.GetOrCreateSnapshotAsync(process.Id, "System", cancellationToken).ConfigureAwait(false);
            
            // 更新Deployment记录快照ID
            deployment.ProcessSnapshotId = processSnapshot.Id;
            await _deploymentDataProvider.UpdateDeploymentAsync(deployment, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        
        var plan = new DeploymentPlanDto
        {
            DeploymentId = deploymentId,
            ProcessSnapshot = processSnapshot
        };

        return plan;
    }
}
