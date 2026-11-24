using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Runbooks;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Runbooks.Exceptions;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Models.Deployments.Snapshots;
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

    Task<RunbookPublishedSnapshotDto> PublishRunbookAsync(
        PublishRunbookCommand command,
        CancellationToken cancellationToken = default);

    Task<RunbookDto> GetRunbookAsync(
        GetRunbookRequest request,
        CancellationToken cancellationToken = default);

    Task<GetRunbooksResponseData> GetRunbooksAsync(
        GetRunbooksRequest request,
        CancellationToken cancellationToken = default);

    Task<GetRunbookEligibleEnvironmentsResponseData> GetEligibleEnvironmentsAsync(
        GetRunbookEligibleEnvironmentsRequest request,
        CancellationToken cancellationToken = default);

    Task<RunbookRunDto> RunAsync(
        RunRunbookCommand command,
        CancellationToken cancellationToken = default);
}

public class RunbookService(
    IRunbookDataProvider runbookDataProvider,
    IRunbookAggregateDataProvider aggregateDataProvider,
    IProjectDataProvider projectDataProvider,
    IAuthorizationService authorizationService,
    ICurrentUser currentUser,
    IRunbookPublicationDataProvider publicationDataProvider,
    IEnvironmentDataProvider environmentDataProvider,
    IMachineDataProvider machineDataProvider,
    IRunbookRunDataProvider runbookRunDataProvider,
    IServerTaskDataProvider serverTaskDataProvider,
    ISquidBackgroundJobClient backgroundJobClient) : IRunbookService
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

        var spaceId = model.SpaceId;
        if (spaceId <= 0)
        {
            throw new RunbookValidationException(
                "runbook.space_required",
                nameof(model.SpaceId),
                "Runbook space is required.");
        }
        var project = await ResolveProjectAsync(model.ProjectId, spaceId, cancellationToken)
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

        var spaceId = command.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(command.SpaceId),
                "Runbook space is required.");
        var project = await ResolveProjectAsync(command.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        var runbook = await GetOwnedRunbookAsync(
            command.Id,
            command.ProjectId,
            spaceId,
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
        var spaceId = command.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(command.SpaceId),
                "Runbook space is required.");
        var project = await ResolveProjectAsync(command.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        var source = await GetOwnedRunbookAsync(
            command.SourceRunbookId,
            command.ProjectId,
            spaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(source.ProjectId, source.SpaceId, Permission.RunbookEdit, cancellationToken)
            .ConfigureAwait(false);
        var name = RunbookInputNormalizer.NormalizeCopyName(source.Name, command.Name);

        try
        {
            var copy = await aggregateDataProvider.CopyAsync(
                source.Id,
                command.ProjectId,
                spaceId,
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
        var spaceId = command.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(command.SpaceId),
                "Runbook space is required.");
        var project = await ResolveProjectAsync(command.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookEdit, cancellationToken).ConfigureAwait(false);
        await GetOwnedRunbookAsync(
            command.Id,
            command.ProjectId,
            spaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(project.Id, project.SpaceId, Permission.RunbookEdit, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await aggregateDataProvider.DeleteAsync(
                command.Id, command.ProjectId, spaceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (RunbookDbExceptionMapper.IsPublishedDeleteConflict(ex))
        {
            throw RunbookConflictException.PublishedDelete(command.Id);
        }

        return new DeleteRunbookResponseData { Id = command.Id };
    }

    public async Task<RunbookPublishedSnapshotDto> PublishRunbookAsync(
        PublishRunbookCommand command, CancellationToken cancellationToken = default)
    {
        var spaceId = command.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required", nameof(command.SpaceId), "Runbook space is required.");
        var project = await ResolveProjectAsync(command.ProjectId, spaceId, cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookPublish, cancellationToken).ConfigureAwait(false);

        return await publicationDataProvider.PublishAsync(
            command.Id, project.Id, project.SpaceId,
            currentUser.Id ?? CurrentUsers.InternalUser.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunbookDto> GetRunbookAsync(
        GetRunbookRequest request,
        CancellationToken cancellationToken = default)
    {
        var spaceId = request.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(request.SpaceId),
                "Runbook space is required.");
        await ResolveProjectAsync(request.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        var runbook = await GetOwnedRunbookAsync(
            request.Id,
            request.ProjectId,
            spaceId,
            cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(runbook.ProjectId, runbook.SpaceId, Permission.RunbookView, cancellationToken)
            .ConfigureAwait(false);

        var dto = ToDto(runbook);
        if (runbook.PublishedSnapshotId is int snapshotId)
            dto.PublishedSnapshot = await publicationDataProvider
                .GetPublishedAsync(runbook.Id, snapshotId, cancellationToken).ConfigureAwait(false);

        return dto;
    }

    public async Task<GetRunbooksResponseData> GetRunbooksAsync(
        GetRunbooksRequest request,
        CancellationToken cancellationToken = default)
    {
        var spaceId = request.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(request.SpaceId),
                "Runbook space is required.");
        var project = await ResolveProjectAsync(request.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project, Permission.RunbookView, cancellationToken).ConfigureAwait(false);
        var (pageIndex, pageSize) = RunbookInputNormalizer.NormalizePaging(
            request.PageIndex,
            request.PageSize);
        var (count, runbooks) = await runbookDataProvider.GetRunbookPagingAsync(
            request.ProjectId,
            spaceId,
            pageIndex,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        return new GetRunbooksResponseData
        {
            Count = count,
            Runbooks = runbooks.Select(ToDto).ToList()
        };
    }

    public async Task<GetRunbookEligibleEnvironmentsResponseData> GetEligibleEnvironmentsAsync(
        GetRunbookEligibleEnvironmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var spaceId = request.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(request.SpaceId),
                "Runbook space is required.");

        var project = await ResolveProjectAsync(request.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project.Id, project.SpaceId, Permission.RunbookRun, cancellationToken,
                projectGroupId: project.ProjectGroupId)
            .ConfigureAwait(false);

        var runbook = await GetOwnedRunbookAsync(
            request.Id,
            request.ProjectId,
            spaceId,
            cancellationToken).ConfigureAwait(false);

        // An unpublished draft is valid Runbook state, but it has no executable
        // version and therefore has no eligible Run Now environments.
        if (runbook.PublishedSnapshotId is not int snapshotId)
            return new GetRunbookEligibleEnvironmentsResponseData();

        var published = await publicationDataProvider
            .GetPublishedDataAsync(runbook.Id, snapshotId, cancellationToken)
            .ConfigureAwait(false);

        // The pointer is protected by a composite foreign key. Keep the query
        // fail-closed if old or manually altered data ever leaves it dangling.
        if (published?.Process == null)
            return new GetRunbookEligibleEnvironmentsResponseData();

        var environments = (await environmentDataProvider
                .GetEnvironmentPagingAsync(spaceId, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            .Item2;
        if (environments.Count == 0)
            return new GetRunbookEligibleEnvironmentsResponseData();

        var machines = await machineDataProvider
            .GetMachinesByFilterAsync(environments.Select(environment => environment.Id).ToHashSet(), [], cancellationToken)
            .ConfigureAwait(false);

        var result = new GetRunbookEligibleEnvironmentsResponseData();
        foreach (var environment in environments.OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ThenBy(e => e.Id))
        {
            if (!IsEnvironmentEligible(published.Process, environment, machines))
                continue;

            if (!currentUser.IsInternal)
            {
                var permission = await authorizationService.CheckPermissionAsync(new PermissionCheckRequest
                {
                    UserId = currentUser.Id.Value,
                    Permission = Permission.RunbookRun,
                    ProjectId = project.Id,
                    ProjectGroupId = project.ProjectGroupId,
                    SpaceId = project.SpaceId,
                    EnvironmentId = environment.Id
                }, cancellationToken).ConfigureAwait(false);
                if (!permission.IsAuthorized)
                    continue;
            }

            result.Environments.Add(ToEnvironmentDto(environment));
        }

        return result;
    }

    public async Task<RunbookRunDto> RunAsync(
        RunRunbookCommand command,
        CancellationToken cancellationToken = default)
    {
        var spaceId = command.SpaceId
            ?? throw new RunbookValidationException(
                "runbook.space_required",
                nameof(command.SpaceId),
                "Runbook space is required.");

        var project = await ResolveProjectAsync(command.ProjectId, spaceId, cancellationToken)
            .ConfigureAwait(false);
        await EnsurePermissionAsync(project.Id, project.SpaceId, Permission.RunbookRun, cancellationToken,
                environmentId: command.EnvironmentId, projectGroupId: project.ProjectGroupId)
            .ConfigureAwait(false);

        var runbook = await GetOwnedRunbookAsync(
            command.Id,
            command.ProjectId,
            spaceId,
            cancellationToken).ConfigureAwait(false);

        if (runbook.PublishedSnapshotId is not int snapshotId)
        {
            throw new RunbookValidationException(
                "runbook.not_published",
                nameof(command.Id),
                "Runbook must have a published version before it can run.");
        }

        var published = await publicationDataProvider
            .GetPublishedDataAsync(runbook.Id, snapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (published?.Process == null)
        {
            throw new RunbookValidationException(
                "runbook.published_snapshot_missing",
                nameof(command.Id),
                "The current published Runbook version is unavailable.");
        }

        // Revalidate the immutable payload at acceptance. This keeps the API
        // fail-closed if a snapshot was imported or altered outside the publish path.
        RunbookPublicationValidator.EnsureSupported(published.Process);

        var environment = await environmentDataProvider
            .GetEnvironmentByIdAsync(command.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);
        if (environment == null || environment.SpaceId != project.SpaceId)
            throw new DeploymentEntityNotFoundException("Environment", command.EnvironmentId);

        var machines = await machineDataProvider
            .GetMachinesByFilterAsync([environment.Id], [], cancellationToken).ConfigureAwait(false);
        if (!IsEnvironmentEligible(published.Process, environment, machines))
        {
            throw new RunbookValidationException(
                "runbook.environment_ineligible",
                nameof(command.EnvironmentId),
                $"Environment {command.EnvironmentId} is not eligible for this Runbook version.");
        }

        var initiatedBy = currentUser.Id ?? CurrentUsers.InternalUser.Id;
        var task = new Persistence.Entities.Deployments.ServerTask
        {
            Name = "Runbook",
            Description = $"Run Runbook {runbook.Name} version {published.Snapshot.Version}",
            QueueTime = DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = RunbookTaskTypes.Runbook,
            SpaceId = spaceId,
            ProjectId = project.Id,
            EnvironmentId = command.EnvironmentId,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            DataVersion = Guid.NewGuid().ToByteArray()
        };
        var run = new Persistence.Entities.Deployments.RunbookRun
        {
            RunbookId = runbook.Id,
            SnapshotId = published.Snapshot.Id,
            ProjectId = project.Id,
            SpaceId = spaceId,
            EnvironmentId = command.EnvironmentId,
            InitiatedBy = initiatedBy,
            Task = task
        };

        // The pair is committed before Hangfire sees the task. A worker can
        // therefore always resolve the pinned RunbookRun by ServerTaskId.
        run = await runbookRunDataProvider
            .CreateAsync(run, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var jobId = backgroundJobClient.Enqueue<IRunbookTaskExecutor>(
                executor => executor.ProcessAsync(task.Id, CancellationToken.None));
            if (string.IsNullOrEmpty(jobId))
                throw new InvalidOperationException("The Runbook execution job was not accepted by the background queue.");

            await serverTaskDataProvider
                .SetJobIdAsync(task.Id, jobId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await serverTaskDataProvider.FailPendingRunbookDispatchAsync(task.Id,
                    $"Runbook execution could not be dispatched: {exception.Message}", CancellationToken.None)
                .ConfigureAwait(false);
            var currentTask = await serverTaskDataProvider
                .GetServerTaskByIdNoTrackingAsync(task.Id, CancellationToken.None)
                .ConfigureAwait(false);
            task.State = currentTask?.State ?? TaskState.Failed;
        }

        return new RunbookRunDto
        {
            Id = run.Id,
            RunbookId = run.RunbookId,
            SnapshotId = run.SnapshotId,
            SnapshotVersion = published.Snapshot.Version,
            TaskId = run.ServerTaskId,
            ProjectId = run.ProjectId,
            SpaceId = run.SpaceId,
            EnvironmentId = run.EnvironmentId,
            InitiatedBy = run.InitiatedBy,
            State = task.State,
            CreatedDate = run.CreatedDate
        };
    }

    private static bool IsEnvironmentEligible(DeploymentProcessSnapshotDataDto process,
        Persistence.Entities.Deployments.Environment environment,
        List<Machine> machines)
    {
        var actions = process.StepSnapshots
            .Where(step => !step.IsDisabled)
            .SelectMany(step => step.ActionSnapshots.Where(action => !action.IsDisabled)
                .Select(action => (Step: step, Action: action)))
            .ToList();

        return actions.Count > 0 && actions.All(pair =>
            IsActionInEnvironment(pair.Action, environment.Id)
            && HasEligibleTarget(pair.Step, pair.Action, environment, machines));
    }

    private static bool IsActionInEnvironment(
        DeploymentActionSnapshotDataDto action,
        int environmentId)
    {
        if (action.ExcludedEnvironments.Contains(environmentId))
            return false;

        return action.Environments.Count == 0 || action.Environments.Contains(environmentId);
    }

    private static bool HasEligibleTarget(
        DeploymentStepSnapshotDataDto step,
        DeploymentActionSnapshotDataDto action,
        Persistence.Entities.Deployments.Environment environment,
        List<Persistence.Entities.Deployments.Machine> machines)
    {
        var requiredRoles = action.MachineRoles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stepRoles = DeploymentTargetFinder.ParseCsvRoles(
            step.Properties.GetValueOrDefault(SpecialVariables.Step.TargetRoles)
            ?? step.Properties.GetValueOrDefault("TargetRoles"));

        return machines.Any(machine =>
            machine.SpaceId == environment.SpaceId
            && !machine.IsDisabled
            && DeploymentTargetFinder.ParseIds(machine.EnvironmentIds).Contains(environment.Id)
            && CommunicationStyleParser.Parse(machine.Endpoint) == CommunicationStyle.TentacleListening
            && EndpointJsonHelper.ParseTentacleListeningEndpoint(machine.Endpoint) != null
            && MatchesRoles(machine, stepRoles, requiredRoles));
    }

    private static bool MatchesRoles(Machine machine, HashSet<string> stepRoles, HashSet<string> actionRoles)
    {
        var machineRoles = DeploymentTargetFinder.ParseRoles(machine.Roles);
        return (stepRoles.Count == 0 || machineRoles.Overlaps(stepRoles))
            && (actionRoles.Count == 0 || machineRoles.Overlaps(actionRoles));
    }

    private static EnvironmentDto ToEnvironmentDto(Persistence.Entities.Deployments.Environment environment) =>
        new()
        {
            Id = environment.Id,
            SpaceId = environment.SpaceId,
            Slug = environment.Slug,
            Name = environment.Name,
            Description = environment.Description,
            SortOrder = environment.SortOrder,
            UseGuidedFailure = environment.UseGuidedFailure,
            AllowDynamicInfrastructure = environment.AllowDynamicInfrastructure,
            LastModifiedDate = environment.LastModifiedDate,
            LastModifiedBy = environment.LastModifiedBy
        };

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
        CancellationToken cancellationToken,
        int? environmentId = null,
        int? projectGroupId = null)
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
                ProjectId = projectId,
                EnvironmentId = environmentId,
                ProjectGroupId = projectGroupId
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
            PublishedSnapshotId = runbook.PublishedSnapshotId,
            CreatedDate = runbook.CreatedDate,
            CreatedBy = runbook.CreatedBy,
            LastModifiedDate = runbook.LastModifiedDate,
            LastModifiedBy = runbook.LastModifiedBy
        };
    }
}
