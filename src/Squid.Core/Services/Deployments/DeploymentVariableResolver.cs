using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public class DeploymentVariableResolver : IDeploymentVariableResolver
{
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IVariableSetDataProvider _variableSetDataProvider;
    private readonly IHybridVariableSnapshotService _variableSnapshotService;

    public DeploymentVariableResolver(
        IProjectDataProvider projectDataProvider,
        IDeploymentDataProvider deploymentDataProvider,
        IVariableSetDataProvider variableSetDataProvider,
        IHybridVariableSnapshotService variableSnapshotService)
    {
        _projectDataProvider = projectDataProvider;
        _deploymentDataProvider = deploymentDataProvider;
        _variableSetDataProvider = variableSetDataProvider;
        _variableSnapshotService = variableSnapshotService;
    }

    public async Task<List<VariableDto>> ResolveVariablesAsync(int deploymentId, CancellationToken cancellationToken)
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

        List<VariableDto> variables;
        
        // 获取或创建变量快照
        if (deployment.VariableSetSnapshotId.HasValue)
        {
            variables = (await _variableSnapshotService.LoadSnapshotAsync(deployment.VariableSetSnapshotId.Value, cancellationToken).ConfigureAwait(false)).Variables;
        }
        else
        {
            // 如果没有快照ID，查找项目的变量集并创建快照
            var projectVariableSets = await _variableSetDataProvider.GetVariableSetsByIdAsync([project.VariableSetId], cancellationToken).ConfigureAwait(false);
            var libraryVariableSets = await _variableSetDataProvider.GetVariableSetsByIdAsync(
                project.IncludedLibraryVariableSetIds.Split(',').Select(int.Parse).ToList(), cancellationToken).ConfigureAwait(false);
            var allVariableSets = libraryVariableSets.ConvertAll(x => x.Id);
            
            if (projectVariableSets != null) allVariableSets.AddRange(projectVariableSets.ConvertAll(x => x.Id));
        
            if (allVariableSets.Count == 0) throw new InvalidOperationException($"Variable set not found on project {project.Id}.");

            var variableSetSnapshot = await _variableSnapshotService.GetOrCreateSnapshotAsync(allVariableSets, "System", cancellationToken).ConfigureAwait(false);
            variables = variableSetSnapshot.Variables;

            // 更新Deployment记录快照ID
            deployment.VariableSetSnapshotId = variableSetSnapshot.Id;
            await _deploymentDataProvider.UpdateDeploymentAsync(deployment, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        EnhanceVariablesForK8s(variables);
        
        return variables;
    }

    private void EnhanceVariablesForK8s(List<VariableDto> variables)
    {
        variables.AddRange([
            new VariableDto
            {
                Name = "Octopus.Action.EnabledFeatures",
                Value = "Octopus.Features.SubstituteInFiles",
                Description = null,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.KubernetesContainers.CustomResourceYamlFileName",
                Value = "content/*.yaml",
                Description = null,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.SubstituteInFiles.TargetFiles",
                Value = "content/*.yaml",
                Description = null,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.Kubernetes.ResourceStatusCheck",
                Value = "False",
                Description = null,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            },
            new VariableDto
            {
                Name = "Octopus.Action.Kubernetes.DeploymentTimeout",
                Value = "600",
                Description = null,
                Type = VariableType.String,
                IsSensitive = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "System"
            }
        ]);
    }
}
