using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Requests.Mcp;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.Mcp;

public sealed record ScopedDeploymentHistoryResult(
    ScopedReadPage<DeploymentReadItem> Page,
    DeploymentReadItem? LatestSuccessfulDeployment);

public partial interface IScopedReadQueries
{
    Task<ScopedDeploymentHistoryResult?> ListVisibleDeploymentsAsync(ListVisibleDeploymentsRequest request,
        CancellationToken cancellationToken = default);

    Task<DeploymentTaskReadItem?> GetVisibleDeploymentTaskAsync(GetVisibleDeploymentTaskRequest request,
        CancellationToken cancellationToken = default);
}

public partial class ScopedReadQueries
{
    private const string SuccessStateKey = "SUCCESS";

    public async Task<ScopedDeploymentHistoryResult?> ListVisibleDeploymentsAsync(
        ListVisibleDeploymentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = ValidatePageSize(request.PageSize);
        var grants = await GetGrantsAsync(Permission.DeploymentView, cancellationToken).ConfigureAwait(false);

        if (grants.Count == 0) return null;

        var scopes = BuildAuthorizedDeploymentScopeQuery(grants, request.SpaceId, request.ProjectId,
            request.EnvironmentId);

        if (!await scopes.AnyAsync(cancellationToken).ConfigureAwait(false)) return null;

        var history = BuildDeploymentHistoryQuery(scopes);

        var latestSuccessfulRow = await history
            .Where(row => row.HasMatchingTask && row.TaskStateValue != null &&
                          row.TaskStateValue.ToUpper() == SuccessStateKey)
            .OrderByDescending(row => row.CreatedDate)
            .ThenByDescending(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (request.After != null)
        {
            if (request.After.CreatedAt == null)
                throw new ArgumentException("Deployment cursors require CreatedAt.", nameof(request));

            var afterCreatedAt = request.After.CreatedAt.Value;
            history = history.Where(row => row.CreatedDate < afterCreatedAt ||
                                           (row.CreatedDate == afterCreatedAt && row.Id < request.After.Id));
        }

        var rows = await history
            .OrderByDescending(row => row.CreatedDate)
            .ThenByDescending(row => row.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(MapDeploymentItem).ToList();
        var page = BuildPage(items, pageSize, item => new ScopedReadPosition(item.Id, item.CreatedAt));

        return new ScopedDeploymentHistoryResult(
            page,
            latestSuccessfulRow == null ? null : MapDeploymentItem(latestSuccessfulRow));
    }

    public async Task<DeploymentTaskReadItem?> GetVisibleDeploymentTaskAsync(
        GetVisibleDeploymentTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var grants = await GetGrantsAsync(Permission.TaskView, cancellationToken).ConfigureAwait(false);

        if (grants.Count == 0) return null;

        var accessRows = from deployment in repository.QueryNoTracking<Deployment>()
            where deployment.TaskId == request.TaskId && deployment.SpaceId == request.SpaceId
            join space in repository.QueryNoTracking<Space>() on deployment.SpaceId equals space.Id
            join project in repository.QueryNoTracking<Project>() on
                new { ProjectId = deployment.ProjectId, SpaceId = deployment.SpaceId } equals
                new { ProjectId = project.Id, SpaceId = project.SpaceId }
            join projectGroup in repository.QueryNoTracking<ProjectGroup>() on
                new { ProjectGroupId = project.ProjectGroupId, SpaceId = project.SpaceId } equals
                new { ProjectGroupId = projectGroup.Id, SpaceId = projectGroup.SpaceId }
            join environment in repository.QueryNoTracking<Environment>() on
                new { EnvironmentId = deployment.EnvironmentId, SpaceId = deployment.SpaceId } equals
                new { EnvironmentId = environment.Id, SpaceId = environment.SpaceId }
            join task in repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>() on
                deployment.TaskId equals (int?)task.Id
            join uniqueDeployment in QueryUniquelyAssociatedDeployments() on deployment.Id equals uniqueDeployment.Id
            where task.ProjectId == project.Id &&
                  task.EnvironmentId == environment.Id &&
                  (!task.SpaceId.HasValue || task.SpaceId == space.Id)
            select new DeploymentTaskAccessRow
            {
                TaskId = task.Id,
                DeploymentId = deployment.Id,
                State = task.State,
                StartedAt = task.StartTime,
                CompletedAt = task.CompletedTime,
                SpaceId = space.Id,
                ProjectId = project.Id,
                ProjectGroupId = projectGroup.Id,
                EnvironmentId = environment.Id
            };

        var accessRow = await ScopedReadAuthorizationFilter
            .Apply(accessRows, grants, row => row.SpaceId, row => row.ProjectId, row => row.ProjectGroupId,
                row => row.EnvironmentId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (accessRow == null) return null;

        return new DeploymentTaskReadItem(
            accessRow.TaskId,
            accessRow.DeploymentId,
            NormalizeTaskState(accessRow.State),
            accessRow.StartedAt,
            accessRow.CompletedAt);
    }

    private IQueryable<DeploymentScopeRow> BuildAuthorizedDeploymentScopeQuery(
        IReadOnlyList<ResourceScopeGrant> grants,
        int spaceId,
        int projectId,
        int environmentId)
    {
        var scopes = from space in repository.QueryNoTracking<Space>()
            join project in repository.QueryNoTracking<Project>() on space.Id equals project.SpaceId
            join projectGroup in repository.QueryNoTracking<ProjectGroup>() on
                new { ProjectGroupId = project.ProjectGroupId, SpaceId = project.SpaceId } equals
                new { ProjectGroupId = projectGroup.Id, SpaceId = projectGroup.SpaceId }
            join environment in repository.QueryNoTracking<Environment>() on space.Id equals environment.SpaceId
            where space.Id == spaceId && project.Id == projectId && environment.Id == environmentId
            select new DeploymentScopeRow
            {
                SpaceId = space.Id,
                ProjectId = project.Id,
                ProjectGroupId = projectGroup.Id,
                EnvironmentId = environment.Id
            };

        return ScopedReadAuthorizationFilter.Apply(scopes, grants, row => row.SpaceId, row => row.ProjectId,
            row => row.ProjectGroupId, row => row.EnvironmentId);
    }

    private IQueryable<Deployment> QueryUniquelyAssociatedDeployments()
    {
        return repository.QueryNoTracking<Deployment>()
            .Where(deployment => deployment.TaskId.HasValue &&
                                 !repository.QueryNoTracking<Deployment>(null).Any(other =>
                                     other.TaskId == deployment.TaskId && other.Id != deployment.Id));
    }

    private IQueryable<DeploymentHistoryRow> BuildDeploymentHistoryQuery(IQueryable<DeploymentScopeRow> scopes)
    {
        return from scope in scopes
            join deployment in repository.QueryNoTracking<Deployment>() on
                new { SpaceId = scope.SpaceId, ProjectId = scope.ProjectId, EnvironmentId = scope.EnvironmentId } equals
                new
                {
                    SpaceId = deployment.SpaceId,
                    ProjectId = deployment.ProjectId,
                    EnvironmentId = deployment.EnvironmentId
                }
            join release in repository.QueryNoTracking<Release>() on deployment.ReleaseId equals release.Id into releases
            from release in releases.DefaultIfEmpty()
            join task in repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>() on
                deployment.TaskId equals (int?)task.Id into tasks
            from task in tasks.DefaultIfEmpty()
            join uniqueDeployment in QueryUniquelyAssociatedDeployments() on deployment.Id equals uniqueDeployment.Id
                into uniqueDeployments
            from uniqueDeployment in uniqueDeployments.DefaultIfEmpty()
            select new DeploymentHistoryRow
            {
                Id = deployment.Id,
                ReleaseId = deployment.ReleaseId,
                CurrentReleaseVersion = release != null && release.SpaceId == scope.SpaceId &&
                                        release.ProjectId == scope.ProjectId
                    ? release.Version
                    : null,
                TaskId = deployment.TaskId,
                HasMatchingTask = task != null && uniqueDeployment != null &&
                                  task.ProjectId == scope.ProjectId &&
                                  task.EnvironmentId == scope.EnvironmentId &&
                                  (!task.SpaceId.HasValue || task.SpaceId == scope.SpaceId),
                TaskStateValue = task == null ? null : task.State,
                TaskCompletedTime = task == null ? null : task.CompletedTime,
                CreatedDate = deployment.CreatedDate
            };
    }

    private static DeploymentReadItem MapDeploymentItem(DeploymentHistoryRow row)
    {
        return new DeploymentReadItem(
            row.Id,
            row.ReleaseId,
            row.CurrentReleaseVersion,
            row.TaskId,
            row.HasMatchingTask ? NormalizeTaskState(row.TaskStateValue) : DeploymentReadState.Unknown,
            row.CreatedDate,
            row.HasMatchingTask ? row.TaskCompletedTime : null);
    }

    private static string NormalizeTaskState(string state)
    {
        return state?.ToUpperInvariant() switch
        {
            "PENDING" => TaskState.Pending,
            "EXECUTING" => TaskState.Executing,
            "SUCCESS" => TaskState.Success,
            "FAILED" => TaskState.Failed,
            "CANCELLING" => TaskState.Cancelling,
            "CANCELLED" => TaskState.Cancelled,
            "TIMEDOUT" => TaskState.TimedOut,
            "PAUSED" => TaskState.Paused,
            _ => DeploymentReadState.Unknown
        };
    }

    private sealed class DeploymentScopeRow
    {
        public int SpaceId { get; init; }
        public int ProjectId { get; init; }
        public int ProjectGroupId { get; init; }
        public int EnvironmentId { get; init; }
    }

    private sealed class DeploymentTaskAccessRow
    {
        public int TaskId { get; init; }
        public int DeploymentId { get; init; }
        public string? State { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public int ProjectId { get; init; }
        public int ProjectGroupId { get; init; }
        public int SpaceId { get; init; }
        public int EnvironmentId { get; init; }
    }

    private sealed class DeploymentHistoryRow
    {
        public int Id { get; init; }
        public int ReleaseId { get; init; }
        public string? CurrentReleaseVersion { get; init; }
        public int? TaskId { get; init; }
        public bool HasMatchingTask { get; init; }
        public string? TaskStateValue { get; init; }
        public DateTimeOffset? TaskCompletedTime { get; init; }
        public DateTimeOffset CreatedDate { get; init; }
    }
}

internal static class DeploymentReadState
{
    public const string Unknown = "Unknown";
}
