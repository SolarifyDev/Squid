using Squid.Core.DependencyInjection;
using Squid.Message.Models.Deployments.Project;

namespace Squid.Core.Services.Deployments.Project;

public interface IProjectDeploymentSettingsService : IScopedDependency
{
    Task<DeploymentSettingsDto> GetAsync(int projectId, CancellationToken cancellationToken = default);

    Task<DeploymentSettingsDto> SaveAsync(int projectId, DeploymentSettingsDto settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes a project's <see cref="DeploymentSettingsDto"/> (persisted on the
/// project's <c>DeploymentSettingsJson</c> column). The deploy pipeline reads the same
/// column directly; this service is the operator-facing get/save surface.
/// </summary>
public sealed class ProjectDeploymentSettingsService(IProjectDataProvider projectDataProvider) : IProjectDeploymentSettingsService
{
    public async Task<DeploymentSettingsDto> GetAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        return DeploymentSettingsSerializer.Deserialize(project.DeploymentSettingsJson);
    }

    public async Task<DeploymentSettingsDto> SaveAsync(int projectId, DeploymentSettingsDto settings, CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        project.DeploymentSettingsJson = DeploymentSettingsSerializer.Serialize(settings);

        await projectDataProvider.UpdateProjectAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);

        return DeploymentSettingsSerializer.Deserialize(project.DeploymentSettingsJson);
    }

    private async Task<Persistence.Entities.Deployments.Project> LoadProjectAsync(int projectId, CancellationToken cancellationToken)
    {
        var project = await projectDataProvider.GetProjectByIdAsync(projectId, cancellationToken).ConfigureAwait(false);

        if (project == null)
            throw new InvalidOperationException($"Project {projectId} not found");

        return project;
    }
}
