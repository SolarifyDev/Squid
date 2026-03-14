using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.Deployments.Variables;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

public class DeploymentVariableResolver : IDeploymentVariableResolver
{
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;
    private readonly ILibraryVariableSetDataProvider _libraryVariableSetDataProvider;

    public DeploymentVariableResolver(
        IProjectDataProvider projectDataProvider,
        IDeploymentDataProvider deploymentDataProvider,
        IDeploymentSnapshotService deploymentSnapshotService,
        ILibraryVariableSetDataProvider libraryVariableSetDataProvider)
    {
        _projectDataProvider = projectDataProvider;
        _deploymentDataProvider = deploymentDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
        _libraryVariableSetDataProvider = libraryVariableSetDataProvider;
    }

    public async Task<List<VariableDto>> ResolveVariablesAsync(int deploymentId, CancellationToken cancellationToken)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId, cancellationToken).ConfigureAwait(false);

        if (deployment == null)
            throw new DeploymentEntityNotFoundException("Deployment", deploymentId);

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
            throw new DeploymentEntityNotFoundException("Project", deployment.ProjectId);

        var variableSetIds = await ResolveAllVariableSetIdsAsync(project, ct).ConfigureAwait(false);

        if (variableSetIds.Count == 0)
            throw new DeploymentEntityNotFoundException("VariableSet", project.Id, $"No variable sets configured on project");

        var snapshot = await _deploymentSnapshotService
            .SnapshotVariableSetFromIdsAsync(variableSetIds, ct).ConfigureAwait(false);

        deployment.VariableSetSnapshotId = snapshot.Id;
        await _deploymentDataProvider.UpdateDeploymentAsync(deployment, cancellationToken: ct).ConfigureAwait(false);

        return snapshot.Data.Variables;
    }

    private async Task<List<int>> ResolveAllVariableSetIdsAsync(
        Persistence.Entities.Deployments.Project project, CancellationToken ct)
    {
        var variableSetIds = new List<int> { project.VariableSetId };

        var libraryIds = project.GetIncludedLibraryVariableSetIdList();

        if (libraryIds.Count > 0)
        {
            var libraryVariableSets = await _libraryVariableSetDataProvider
                .GetByIdsAsync(libraryIds, ct).ConfigureAwait(false);

            variableSetIds.AddRange(libraryVariableSets.Select(lvs => lvs.VariableSetId));
        }

        return variableSetIds;
    }
}
