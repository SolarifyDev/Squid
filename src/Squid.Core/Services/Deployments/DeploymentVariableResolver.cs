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

    public async Task<VariableSetSnapshotData> ResolveVariablesAsync(int deploymentId)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId);

        if (deployment == null)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _projectDataProvider.GetProjectByIdAsync(deployment.ProjectId);

        if (project == null)
        {
            throw new InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }

        // 获取或创建变量快照
        VariableSetSnapshotData variableSnapshot;

        if (deployment.VariableSnapshotId.HasValue)
        {
            // 如果已有快照ID，直接加载快照
            variableSnapshot = await _variableSnapshotService.LoadSnapshotAsync(deployment.VariableSnapshotId.Value);
        }
        else
        {
            // 如果没有快照ID，查找项目的变量集并创建快照
            var variableSet = await _variableSetDataProvider.GetVariableSetByOwnerAsync(project.Id, VariableSetOwnerType.Project);

            if (variableSet != null)
            {
                var snapshotId = await _variableSnapshotService.GetOrCreateSnapshotAsync(variableSet.Id, "System");
                variableSnapshot = await _variableSnapshotService.LoadSnapshotAsync(snapshotId);

                // 更新Deployment记录快照ID
                deployment.VariableSnapshotId = snapshotId;
                await _deploymentDataProvider.UpdateDeploymentAsync(deployment);
            }
            else
            {
                // 如果没有变量集，创建空的快照数据
                variableSnapshot = new VariableSetSnapshotData
                {
                    Id = 0,
                    OwnerId = project.Id,
                    OwnerType = VariableSetOwnerType.Project,
                    Version = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Variables = new List<VariableSnapshotData>(),
                    ScopeDefinitions = new Dictionary<string, List<string>>()
                };
            }
        }

        return variableSnapshot;
    }
}
