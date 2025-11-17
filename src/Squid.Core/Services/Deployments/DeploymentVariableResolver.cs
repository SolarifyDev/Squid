using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.Project;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Core.Services.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public class DeploymentVariableResolver : IDeploymentVariableResolver
{
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IVariableSetDataProvider _variableSetDataProvider;
    private readonly IHybridVariableSnapshotService _variableSnapshotService;

    public DeploymentVariableResolver(
        IDeploymentDataProvider deploymentDataProvider,
        IProjectDataProvider projectDataProvider,
        IVariableSetDataProvider variableSetDataProvider,
        IHybridVariableSnapshotService variableSnapshotService)
    {
        _deploymentDataProvider = deploymentDataProvider;
        _projectDataProvider = projectDataProvider;
        _variableSetDataProvider = variableSetDataProvider;
        _variableSnapshotService = variableSnapshotService;
    }

    public async Task<VariableSetSnapshotData> ResolveVariablesAsync(int deploymentId, CancellationToken cancellationToken)
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

        VariableSetSnapshotData variableSetSnapshot;
        
        // 获取或创建变量快照
        if (deployment.VariableSnapshotId.HasValue)
        {
            variableSetSnapshot = await _variableSnapshotService.LoadSnapshotAsync(deployment.VariableSnapshotId.Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 如果没有快照ID，查找项目的变量集并创建快照
            var variableSet = await _variableSetDataProvider.GetVariableSetByOwnerAsync(project.Id, VariableSetOwnerType.Project, cancellationToken).ConfigureAwait(false);
        
            if (variableSet == null) throw new InvalidOperationException($"Variable set not found on project {project.Id}.");

            variableSetSnapshot = await _variableSnapshotService.GetOrCreateSnapshotAsync(variableSet.Id, "System", cancellationToken).ConfigureAwait(false);
            
            // 更新Deployment记录快照ID
            deployment.VariableSnapshotId = variableSetSnapshot.Id;
            await _deploymentDataProvider.UpdateDeploymentAsync(deployment, cancellationToken: cancellationToken).ConfigureAwait(false);
        }


        return variableSetSnapshot;
    }
}
