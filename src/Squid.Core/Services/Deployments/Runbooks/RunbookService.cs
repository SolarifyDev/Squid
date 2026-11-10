using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Runbooks.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.Core.Services.Deployments.Runbooks;

public interface IRunbookService : IScopedDependency
{
    Task<RunbookDto> CreateRunbookAsync(
        CreateRunbookCommand command,
        CancellationToken cancellationToken = default);

    Task<RunbookDto> UpdateRunbookAsync(
        UpdateRunbookCommand command,
        CancellationToken cancellationToken = default);

    Task<RunbookDto> CopyRunbookAsync(
        CopyRunbookCommand command,
        CancellationToken cancellationToken = default);

    Task<DeleteRunbookResponseData> DeleteRunbookAsync(
        DeleteRunbookCommand command,
        CancellationToken cancellationToken = default);

    Task<RunbookDto> GetRunbookAsync(
        GetRunbookRequest request,
        CancellationToken cancellationToken = default);

    Task<GetRunbooksResponseData> GetRunbooksAsync(
        GetRunbooksRequest request,
        CancellationToken cancellationToken = default);
}

public class RunbookService(
    IRunbookDataProvider runbookDataProvider,
    IRunbookAggregateDataProvider aggregateDataProvider,
    IProjectDataProvider projectDataProvider,
    IAuthorizationService authorizationService,
    ICurrentUser currentUser) : IRunbookService
{
    public async Task<RunbookDto> CreateRunbookAsync(
        CreateRunbookCommand command,
        CancellationToken cancellationToken = default)
    {
        var model = command.Runbook
            ?? throw new RunbookValidationException(
                "runbook.required",
                nameof(command.Runbook),
                "Runbook input is required.");

        var project = await ResolveProjectAsync(model.ProjectId, model.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        var name = RunbookInputNormalizer.NormalizeName(model.Name);
        var description = RunbookInputNormalizer.NormalizeDescription(model.Description);
        var tags = RunbookInputNormalizer.NormalizeTags(model.Tags);

        try
        {
            var runbook = await aggregateDataProvider.CreateAsync(
                project.Id,
                project.SpaceId,
                name,
                description,
                RunbookInputNormalizer.SerializeTags(tags),
                cancellationToken).ConfigureAwait(false);

            return ToDto(runbook);
        }
        catch (Exception ex) when (RunbookDbExceptionMapper.IsNameConflict(ex))
        {
            throw new RunbookNameConflictException(name);
        }
    }

    public async Task<RunbookDto> UpdateRunbookAsync(
        UpdateRunbookCommand command,
        CancellationToken cancellationToken = default)
    {
        var model = command.Runbook
            ?? throw new RunbookValidationException(
                "runbook.required",
                nameof(command.Runbook),
                "Runbook input is required.");

        var project = await ResolveProjectAsync(command.ProjectId, command.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        var runbook = await GetOwnedRunbookAsync(
            command.Id,
            command.ProjectId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(runbook.ProjectId, runbook.SpaceId, Permission.RunbookEdit, cancellationToken)
            .ConfigureAwait(false);

        var name = RunbookInputNormalizer.NormalizeName(model.Name);
        var description = RunbookInputNormalizer.NormalizeDescription(model.Description);
        var tags = RunbookInputNormalizer.NormalizeTags(model.Tags);

        try
        {
            var updated = await aggregateDataProvider.UpdateMetadataAsync(
                runbook,
                name,
                description,
                RunbookInputNormalizer.SerializeTags(tags),
                cancellationToken).ConfigureAwait(false);

            return ToDto(updated);
        }
        catch (Exception ex) when (RunbookDbExceptionMapper.IsNameConflict(ex))
        {
            throw new RunbookNameConflictException(name);
        }
    }

    public async Task<RunbookDto> CopyRunbookAsync(
        CopyRunbookCommand command,
        CancellationToken cancellationToken = default)
    {
        var project = await ResolveProjectAsync(command.ProjectId, command.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        var source = await GetOwnedRunbookAsync(
            command.SourceRunbookId,
            command.ProjectId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(source.ProjectId, source.SpaceId, Permission.RunbookEdit, cancellationToken)
            .ConfigureAwait(false);
        var name = RunbookInputNormalizer.NormalizeCopyName(source.Name, command.Name);

        try
        {
            var copy = await aggregateDataProvider.CopyAsync(
                source.Id,
                command.ProjectId,
                command.SpaceId,
                name,
                cancellationToken).ConfigureAwait(false);

            return ToDto(copy);
        }
        catch (Exception ex) when (RunbookDbExceptionMapper.IsNameConflict(ex))
        {
            throw new RunbookNameConflictException(name);
        }
    }

    public async Task<DeleteRunbookResponseData> DeleteRunbookAsync(
        DeleteRunbookCommand command,
        CancellationToken cancellationToken = default)
    {
        var project = await ResolveProjectAsync(command.ProjectId, command.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        await GetOwnedRunbookAsync(
            command.Id,
            command.ProjectId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(project.Id, project.SpaceId, Permission.RunbookEdit, cancellationToken)
            .ConfigureAwait(false);
        await aggregateDataProvider.DeleteAsync(
            command.Id,
            command.ProjectId,
            command.SpaceId,
            cancellationToken).ConfigureAwait(false);

        return new DeleteRunbookResponseData { Id = command.Id };
    }

    public async Task<RunbookDto> GetRunbookAsync(
        GetRunbookRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await ResolveProjectAsync(request.ProjectId, request.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        var runbook = await GetOwnedRunbookAsync(
            request.Id,
            request.ProjectId,
            request.SpaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(runbook.ProjectId, runbook.SpaceId, Permission.RunbookView, cancellationToken)
            .ConfigureAwait(false);

        return ToDto(runbook);
    }

    public async Task<GetRunbooksResponseData> GetRunbooksAsync(
        GetRunbooksRequest request,
        CancellationToken cancellationToken = default)
    {
        var project = await ResolveProjectAsync(request.ProjectId, request.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookView, cancellationToken).ConfigureAwait(false);
        var (pageIndex, pageSize) = RunbookInputNormalizer.NormalizePaging(
            request.PageIndex,
            request.PageSize);
        var (count, runbooks) = await runbookDataProvider.GetRunbookPagingAsync(
            request.ProjectId,
            request.SpaceId,
            pageIndex,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        return new GetRunbooksResponseData
        {
            Count = count,
            Runbooks = runbooks.Select(ToDto).ToList()
        };
    }

    private async Task<Persistence.Entities.Deployments.Project> ResolveProjectAsync(
        int projectId,
        int spaceId,
        CancellationToken cancellationToken)
    {
        var project = await projectDataProvider.GetProjectByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        if (project == null)
        {
            throw new DeploymentEntityNotFoundException("Project", projectId);
        }

        if (project.SpaceId != spaceId)
        {
            throw new DeploymentEntityNotFoundException(
                "Project",
                projectId,
                "Project does not belong to the requested space.");
        }

        return project;
    }

    private async Task<Runbook> GetOwnedRunbookAsync(
        int runbookId,
        int projectId,
        int spaceId,
        CancellationToken cancellationToken)
    {
        var runbook = await runbookDataProvider.GetRunbookByIdAndProjectAsync(
            runbookId,
            projectId,
            cancellationToken).ConfigureAwait(false);

        if (runbook == null || runbook.SpaceId != spaceId)
        {
            throw new DeploymentEntityNotFoundException("Runbook", runbookId);
        }

        return runbook;
    }

    private async Task EnsurePermissionAsync(
        Persistence.Entities.Deployments.Project project,
        Permission permission,
        CancellationToken cancellationToken) =>
        await EnsurePermissionAsync(project.Id, project.SpaceId, permission, cancellationToken).ConfigureAwait(false);

    private async Task EnsurePermissionAsync(
        int projectId,
        int spaceId,
        Permission permission,
        CancellationToken cancellationToken)
    {
        if (currentUser.IsInternal)
        {
            return;
        }

        if (currentUser.Id == null)
        {
            throw new PermissionDeniedException(
                permission,
                "Authorization rejected: user identity not resolved.");
        }

        await authorizationService.EnsurePermissionAsync(
            new PermissionCheckRequest
            {
                UserId = currentUser.Id.Value,
                Permission = permission,
                SpaceId = spaceId,
                ProjectId = projectId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static RunbookDto ToDto(Runbook runbook)
    {
        return new RunbookDto
        {
            Id = runbook.Id,
            ProjectId = runbook.ProjectId,
            SpaceId = runbook.SpaceId,
            Name = runbook.Name,
            Description = runbook.Description,
            Tags = RunbookInputNormalizer.DeserializeTags(runbook.TagsJson),
            ProcessId = runbook.ProcessId,
            VariableSetId = runbook.VariableSetId,
            CreatedDate = runbook.CreatedDate,
            CreatedBy = runbook.CreatedBy,
            LastModifiedDate = runbook.LastModifiedDate,
            LastModifiedBy = runbook.LastModifiedBy
        };
    }
}
