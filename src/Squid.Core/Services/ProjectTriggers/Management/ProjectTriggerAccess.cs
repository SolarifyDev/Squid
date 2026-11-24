using FluentValidation;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Management;

public interface IProjectTriggerAccess : IScopedDependency
{
    Task EnsureActionAsync(
        int userId, ProjectTriggerDefinition definition, TriggerResourceScope resources, CancellationToken ct);

    Task<Project> EnsureProjectAsync(
        int userId, int projectId, int? spaceId, Permission permission, CancellationToken ct);
}

public sealed class ProjectTriggerAccess : IProjectTriggerAccess
{
    private readonly IProjectTriggerDataProvider _projectTriggerDataProvider;
    private readonly IAuthorizationService _authorizationService;

    public ProjectTriggerAccess(IProjectTriggerDataProvider data, IAuthorizationService authorization)
    {
        _projectTriggerDataProvider = data;
        _authorizationService = authorization;
    }

    public async Task<Project> EnsureProjectAsync(
        int userId, int projectId, int? spaceId, Permission permission, CancellationToken ct)
    {
        if (userId <= 0 || !await _projectTriggerDataProvider.IsExecutionUserActiveAsync(userId, ct).ConfigureAwait(false))
            throw new PermissionDeniedException(permission, "An active user is required.");

        if (projectId <= 0)
            throw new ValidationException("A project ID is required.");

        var project = await _projectTriggerDataProvider.GetProjectAsync(projectId, ct).ConfigureAwait(false);

        if (project == null || (spaceId.HasValue && spaceId.Value != project.SpaceId))
            throw new ProjectTriggerNotFoundException();

        await EnsurePermissionAsync(userId, project, permission, null, ct).ConfigureAwait(false);

        return project;
    }

    public async Task EnsureActionAsync(
        int userId, ProjectTriggerDefinition definition, TriggerResourceScope resources, CancellationToken ct)
    {
        var project = await EnsureProjectAsync(
            userId, definition.ProjectId, definition.SpaceId, Permission.ProjectView, ct).ConfigureAwait(false);

        if (project.IsDisabled)
            throw new ValidationException("The project is disabled.");

        if (resources == null)
            throw new ValidationException("The trigger action requires a resource scope.");

        await EnsureReferencesAsync(project, resources, ct).ConfigureAwait(false);
        await EnsureHistoryPermissionsAsync(userId, project, definition, resources, ct).ConfigureAwait(false);
        await EnsureActionPermissionAsync(userId, project, definition.ActionType, resources, ct).ConfigureAwait(false);
    }

    private async Task EnsureHistoryPermissionsAsync(
        int userId, Project project, ProjectTriggerDefinition definition, TriggerResourceScope resources,
        CancellationToken ct)
    {
        if (resources.SourceEnvironmentId is int source)
            await EnsureHistoryReadAsync(userId, project, source, ct).ConfigureAwait(false);

        // AutoDeploy derives its baseline from the destination environment's own successful deployment history.
        if (definition.ActionType == ProjectTriggerActions.AutoDeploy
            && resources.DestinationEnvironmentId is int baseline && baseline != resources.SourceEnvironmentId)
            await EnsureHistoryReadAsync(userId, project, baseline, ct).ConfigureAwait(false);
    }

    private async Task EnsureActionPermissionAsync(
        int userId, Project project, string actionType, TriggerResourceScope resources, CancellationToken ct)
    {
        switch (actionType)
        {
            case ProjectTriggerActions.CreateRelease:
                await EnsurePermissionAsync(userId, project, Permission.ReleaseCreate, null, ct).ConfigureAwait(false);
                break;
            case ProjectTriggerActions.DeployNewRelease:
                await EnsurePermissionAsync(userId, project, Permission.ReleaseCreate, null, ct).ConfigureAwait(false);
                goto case ProjectTriggerActions.DeployLatestReleaseToEnvironment;
            case ProjectTriggerActions.DeployLatestRelease when resources.SourceEnvironmentId == null:
                throw new ValidationException("A source environment is required for this action.");
            case ProjectTriggerActions.AutoDeploy:
            case ProjectTriggerActions.DeployLatestRelease:
            case ProjectTriggerActions.DeployLatestReleaseToEnvironment:
                if (resources.DestinationEnvironmentId is not int destination)
                    throw new ValidationException("A destination environment is required for this action.");

                await EnsurePermissionAsync(
                    userId, project, Permission.DeploymentCreate, destination, ct).ConfigureAwait(false);
                break;
            default:
                throw new ValidationException("This trigger action is not available.");
        }
    }

    private async Task EnsureReferencesAsync(Project project, TriggerResourceScope scope, CancellationToken ct)
    {
        if (scope.ChannelId is int channel
            && !await _projectTriggerDataProvider.HasChannelAsync(channel, project.Id, project.SpaceId, ct)
                .ConfigureAwait(false))
            throw new ValidationException("The selected channel is unavailable in this project.");

        var ids = new[] { scope.SourceEnvironmentId, scope.DestinationEnvironmentId }.OfType<int>().Distinct().ToArray();

        if (ids.Length == 0) return;

        var count = await _projectTriggerDataProvider.CountEnvironmentsAsync(ids, project.SpaceId, ct)
            .ConfigureAwait(false);

        if (count != ids.Length)
            throw new ValidationException("A selected environment is unavailable in this space.");
    }

    private async Task EnsureHistoryReadAsync(int userId, Project project, int environmentId, CancellationToken ct)
    {
        await EnsurePermissionAsync(userId, project, Permission.EnvironmentView, environmentId, ct).ConfigureAwait(false);
        await EnsurePermissionAsync(userId, project, Permission.DeploymentView, environmentId, ct).ConfigureAwait(false);
    }

    private Task EnsurePermissionAsync(
        int userId, Project project, Permission permission, int? environmentId, CancellationToken ct)
    {
        return _authorizationService.EnsurePermissionAsync(new PermissionCheckRequest
        {
            UserId = userId,
            Permission = permission,
            SpaceId = project.SpaceId,
            ProjectId = project.Id,
            ProjectGroupId = project.ProjectGroupId,
            EnvironmentId = environmentId
        }, ct);
    }
}

public sealed class ProjectTriggerNotFoundException : InvalidOperationException
{
    public ProjectTriggerNotFoundException()
        : base("The requested project trigger or project was not found.")
    {
    }
}

public sealed class ProjectTriggerRevisionConflictException : InvalidOperationException
{
    public ProjectTriggerRevisionConflictException()
        : base("The trigger has changed. Refresh it and retry the operation.")
    {
    }
}
