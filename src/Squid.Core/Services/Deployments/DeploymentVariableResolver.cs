using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public class DeploymentVariableResolver : IDeploymentVariableResolver
{
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;

    public DeploymentVariableResolver(
        IProjectDataProvider projectDataProvider,
        IDeploymentDataProvider deploymentDataProvider,
        IDeploymentSnapshotService deploymentSnapshotService)
    {
        _projectDataProvider = projectDataProvider;
        _deploymentDataProvider = deploymentDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
    }

    public async Task<List<VariableDto>> ResolveVariablesAsync(int deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId, cancellationToken).ConfigureAwait(false);

        if (deployment == null)
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");

        if (deployment.VariableSetSnapshotId.HasValue)
            return await LoadVariablesFromSnapshotAsync(deployment.VariableSetSnapshotId.Value, cancellationToken).ConfigureAwait(false);

        return await SnapshotVariablesFromProjectAsync(deployment, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<VariableDto>> LoadVariablesFromSnapshotAsync(int snapshotId, CancellationToken ct)
    {
        var snapshot = await _deploymentSnapshotService
            .LoadVariableSetSnapshotAsync(snapshotId, ct).ConfigureAwait(false);

        return snapshot.Data.Variables;
    }

    private async Task<List<VariableDto>> SnapshotVariablesFromProjectAsync(
        Persistence.Entities.Deployments.Deployment deployment, CancellationToken ct)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(deployment.ProjectId, ct).ConfigureAwait(false);

        if (project == null)
            throw new InvalidOperationException($"Project {deployment.ProjectId} not found.");

        var variableSetIds = project.GetIncludedLibraryVariableSetIdList();

        if (variableSetIds.Count == 0)
            throw new InvalidOperationException($"Variable set not found on project {project.Id}.");

        var snapshot = await _deploymentSnapshotService
            .SnapshotVariableSetFromIdsAsync(variableSetIds, ct).ConfigureAwait(false);

        deployment.VariableSetSnapshotId = snapshot.Id;
        await _deploymentDataProvider.UpdateDeploymentAsync(deployment, cancellationToken: ct).ConfigureAwait(false);

        return snapshot.Data.Variables;
    }
}
