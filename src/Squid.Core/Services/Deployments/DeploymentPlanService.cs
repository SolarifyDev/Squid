using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.Deployments;

public class DeploymentPlanService : IDeploymentPlanService
{
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;

    public DeploymentPlanService(
        IDeploymentDataProvider deploymentDataProvider,
        IProjectDataProvider projectDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IDeploymentSnapshotService deploymentSnapshotService)
    {
        _deploymentDataProvider = deploymentDataProvider;
        _projectDataProvider = projectDataProvider;
        _processDataProvider = processDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
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
        DeploymentProcessSnapshotDto processSnapshot;
        
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
