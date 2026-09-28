using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.Events;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportDeploymentHistoryImporter : IScopedDependency
{
    Task<OctopusImportDeploymentHistoryResult> ImportAsync(
        OctopusImportDeploymentHistoryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OctopusImportDeploymentHistoryRequest(
    int SpaceId,
    int ProjectId,
    int ChannelId,
    int ReleaseId,
    int EnvironmentId,
    string Name,
    string Json,
    string DeployedBy,
    string DeployedById,
    string DeployedToMachineIdsJson,
    DateTimeOffset Created,
    string TaskName,
    string TaskDescription,
    DateTimeOffset QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? CompletedTime,
    string TaskState,
    string ErrorMessage,
    bool HasWarningsOrErrors,
    int DurationSeconds);

public sealed record OctopusImportDeploymentHistoryResult(int DeploymentId, int TaskId, int CreatedBy);

public sealed class OctopusImportDeploymentHistoryImporter : IOctopusImportDeploymentHistoryImporter
{
    private const string ImportSource = "Octopus";

    private readonly SquidDbContext _dbContext;
    private readonly IRepository _repository;

    public OctopusImportDeploymentHistoryImporter(SquidDbContext dbContext, IRepository repository)
    {
        _dbContext = dbContext;
        _repository = repository;
    }

    public async Task<OctopusImportDeploymentHistoryResult> ImportAsync(
        OctopusImportDeploymentHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);
        request = request with
        {
            Created = request.Created.ToUniversalTime(),
            QueueTime = request.QueueTime.ToUniversalTime(),
            StartTime = request.StartTime?.ToUniversalTime(),
            CompletedTime = request.CompletedTime?.ToUniversalTime()
        };

        var actorId = await ResolveActorIdAsync(request.DeployedBy, cancellationToken).ConfigureAwait(false);
        var completedTime = request.CompletedTime ?? request.StartTime ?? request.QueueTime;
        var taskState = NormalizeTaskState(request.TaskState);
        EnsureTerminalTaskState(taskState);
        var taskJson = BuildTaskJson(request);
        var deploymentJson = BuildDeploymentJson(request);

        var taskId = await InsertServerTaskAsync(
            request,
            actorId,
            taskState,
            taskJson,
            cancellationToken).ConfigureAwait(false);

        var deploymentId = await InsertDeploymentAsync(
            request,
            actorId,
            taskId,
            deploymentJson,
            cancellationToken).ConfigureAwait(false);

        if (string.Equals(taskState, TaskState.Success, StringComparison.OrdinalIgnoreCase))
        {
            await InsertDeploymentCompletionAsync(
                request,
                actorId,
                deploymentId,
                completedTime,
                cancellationToken).ConfigureAwait(false);
        }

        await InsertSummaryLogAsync(
            request,
            actorId,
            taskId,
            taskState,
            completedTime,
            cancellationToken).ConfigureAwait(false);

        await InsertAuditEventAsync(
            request,
            actorId,
            deploymentId,
            taskId,
            taskState,
            completedTime,
            cancellationToken).ConfigureAwait(false);

        return new OctopusImportDeploymentHistoryResult(deploymentId, taskId, actorId);
    }

    private async Task<int> ResolveActorIdAsync(string deployedBy, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(deployedBy))
        {
            var normalized = deployedBy.Trim().ToUpperInvariant();
            var user = await _repository
                .QueryNoTracking<UserAccount>(candidate =>
                    candidate.NormalizedUserName == normalized && !candidate.IsDisabled)
                .Select(candidate => (int?)candidate.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (user is > 0)
                return user.Value;
        }

        return CurrentUsers.InternalUser.Id;
    }

    private async Task<int> InsertServerTaskAsync(
        OctopusImportDeploymentHistoryRequest request,
        int actorId,
        string taskState,
        string taskJson,
        CancellationToken cancellationToken)
    {
        var completedTime = request.CompletedTime ?? request.StartTime ?? request.QueueTime;
        var stateOrder = taskState switch
        {
            TaskState.Success => 30,
            TaskState.Failed => 30,
            TaskState.Cancelled => 30,
            _ => 9
        };

        const string sql = """
            INSERT INTO server_task
                (name, description, queue_time, start_time, completed_time, error_message,
                 concurrency_tag, state, has_warnings_or_errors, server_node_id,
                 project_id, environment_id, duration_seconds, data_version, space_id,
                 last_modified_date, business_process_state, server_task_type,
                 parent_server_task_id, priority_time, state_order, weight, batch_id,
                 "json", job_id, has_pending_interruptions, created_date, created_by,
                 last_modified_by)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5},
                 NULL, {6}, {7}, {8},
                 {9}, {10}, {11}, {12}, {13},
                 {14}, {15}, {16},
                 NULL, {17}, {18}, {19}, 0,
                 {20}, NULL, false, {21}, {22},
                 {22})
            RETURNING id
            """;

        return await InsertReturningIdAsync(
            sql,
            request.TaskName,
            request.TaskDescription,
            request.QueueTime,
            request.StartTime,
            completedTime,
            request.ErrorMessage ?? string.Empty,
            taskState,
            request.HasWarningsOrErrors,
            Guid.Empty,
            request.ProjectId,
            request.EnvironmentId,
            request.DurationSeconds,
            Guid.NewGuid().ToByteArray(),
            request.SpaceId,
            completedTime,
            BusinessProcessState(taskState),
            "Deploy",
            request.QueueTime,
            stateOrder,
            0,
            taskJson,
            request.Created,
            actorId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> InsertDeploymentAsync(
        OctopusImportDeploymentHistoryRequest request,
        int actorId,
        int taskId,
        string deploymentJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO deployment
                (name, task_id, space_id, channel_id, project_id, release_id,
                 environment_id, machine_id, "json", deployed_by, deployed_to_machine_ids,
                 process_snapshot_id, variable_set_snapshot_id, created_date, created_by,
                 last_modified_date, last_modified_by)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5},
                 {6}, 0, {7}, {8}, {9},
                 NULL, NULL, {10}, {8},
                 {10}, {8})
            RETURNING id
            """;

        return await InsertReturningIdAsync(
            sql,
            request.Name,
            taskId,
            request.SpaceId,
            request.ChannelId,
            request.ProjectId,
            request.ReleaseId,
            request.EnvironmentId,
            deploymentJson,
            actorId,
            request.DeployedToMachineIdsJson,
            request.Created,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<int> InsertDeploymentCompletionAsync(
        OctopusImportDeploymentHistoryRequest request,
        int actorId,
        int deploymentId,
        DateTimeOffset completedTime,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO deployment_completion
                (sequence_number, deployment_id, state, completed_time, space_id,
                 created_date, created_by, last_modified_date, last_modified_by)
            VALUES
                (0, {0}, {1}, {2}, {3},
                 {2}, {4}, {2}, {4})
            RETURNING id
            """;

        return InsertReturningIdAsync(
            sql,
            deploymentId,
            TaskState.Success,
            completedTime,
            request.SpaceId,
            actorId,
            cancellationToken);
    }

    private Task<int> InsertSummaryLogAsync(
        OctopusImportDeploymentHistoryRequest request,
        int actorId,
        int taskId,
        string taskState,
        DateTimeOffset completedTime,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO server_task_log
                (server_task_id, category, message_text, detail, source, occurred_at,
                 sequence_number, activity_node_id, created_date, created_by,
                 last_modified_date, last_modified_by)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5},
                 0, NULL, {5}, {6},
                 {5}, {6})
            RETURNING id
            """;

        var category = taskState switch
        {
            TaskState.Failed => ServerTaskLogCategory.Error,
            TaskState.Cancelled => ServerTaskLogCategory.Warning,
            _ => ServerTaskLogCategory.Info
        };

        var message = $"Imported Octopus deployment {taskState} for release to environment {request.EnvironmentId}.";
        var detail = JsonSerializer.Serialize(new
        {
            Source = ImportSource,
            DeployedBy = request.DeployedBy ?? string.Empty,
            DeployedById = request.DeployedById ?? string.Empty,
            request.TaskState,
            ErrorMessage = request.ErrorMessage ?? string.Empty
        });

        return InsertReturningIdAsync(
            sql,
            taskId,
            (int)category,
            message,
            detail,
            ImportSource,
            completedTime,
            actorId,
            cancellationToken);
    }

    private async Task InsertAuditEventAsync(
        OctopusImportDeploymentHistoryRequest request,
        int actorId,
        int deploymentId,
        int taskId,
        string taskState,
        DateTimeOffset completedTime,
        CancellationToken cancellationToken)
    {
        var release = await _repository
            .QueryNoTracking<Release>(candidate => candidate.Id == request.ReleaseId)
            .Select(candidate => candidate.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var environment = await _repository
            .QueryNoTracking<Persistence.Entities.Deployments.Environment>(candidate => candidate.Id == request.EnvironmentId)
            .Select(candidate => candidate.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var project = await _repository
            .QueryNoTracking<Project>(candidate => candidate.Id == request.ProjectId)
            .Select(candidate => candidate.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var category = taskState switch
        {
            TaskState.Success => EventCategory.DeploymentSucceeded,
            TaskState.Failed => EventCategory.DeploymentFailed,
            TaskState.Cancelled => EventCategory.DeploymentCanceled,
            TaskState.TimedOut => EventCategory.DeploymentTimedOut,
            _ => throw new ArgumentException($"Unsupported imported deployment task state '{taskState}'.", nameof(taskState))
        };

        var username = !string.IsNullOrWhiteSpace(request.DeployedBy)
            ? request.DeployedBy.Trim()
            : CurrentUsers.InternalUser.Name;

        var references = JsonSerializer.Serialize(new
        {
            Project = project,
            Release = release,
            Environment = environment
        });

        const string sql = """
            INSERT INTO event
                (category, references_json, space_id, project_id, release_id,
                 deployment_id, environment_id, server_task_id, user_id, username,
                 established_with, occurred)
            VALUES
                ({0}, CAST({1} AS jsonb), {2}, {3}, {4},
                 {5}, {6}, {7}, {8}, {9},
                 {10}, {11})
            """;

        await _dbContext.Database.ExecuteSqlRawAsync(
            sql,
            new object[]
            {
                (short)category,
                references,
                request.SpaceId,
                request.ProjectId,
                request.ReleaseId,
                deploymentId,
                request.EnvironmentId,
                taskId,
                actorId == CurrentUsers.InternalUser.Id ? null : actorId,
                username,
                (short)EventIdentityEstablishedWith.Server,
                completedTime
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> InsertReturningIdAsync(
        string sql,
        params object[] valuesWithCancellationToken)
    {
        var cancellationToken = (CancellationToken)valuesWithCancellationToken[^1];
        var values = valuesWithCancellationToken[..^1];

        var result = await _dbContext.Database
            .SqlQueryRaw<int>(sql, values)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.Single();
    }

    private static string NormalizeTaskState(string state)
    {
        if (string.Equals(state, "Success", StringComparison.OrdinalIgnoreCase))
            return TaskState.Success;

        if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
            return TaskState.Failed;

        if (string.Equals(state, "Canceled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return TaskState.Cancelled;
        }

        return state?.Trim() ?? string.Empty;
    }

    private static string BusinessProcessState(string state)
        => string.Equals(state, TaskState.Success, StringComparison.OrdinalIgnoreCase)
            ? "Completed"
            : "InProgress";

    private static string BuildTaskJson(OctopusImportDeploymentHistoryRequest request)
        => JsonSerializer.Serialize(new
        {
            Octopus = new
            {
                DeployedById = request.DeployedById ?? string.Empty,
                DeployedBy = request.DeployedBy ?? string.Empty,
                request.TaskState
            },
            request.QueueTime,
            request.StartTime,
            request.CompletedTime,
            request.DurationSeconds
        });

    private static string BuildDeploymentJson(OctopusImportDeploymentHistoryRequest request)
        => JsonSerializer.Serialize(new
        {
            Octopus = new
            {
                DeployedById = request.DeployedById ?? string.Empty,
                DeployedBy = request.DeployedBy ?? string.Empty
            }
        });

    private static void EnsureTerminalTaskState(string taskState)
    {
        if (!TaskState.IsTerminal(taskState))
            throw new ArgumentException("Imported deployment tasks must be terminal.", nameof(taskState));
    }

    private static void Validate(OctopusImportDeploymentHistoryRequest request)
    {
        if (request.SpaceId <= 0) throw new ArgumentOutOfRangeException(nameof(request.SpaceId));
        if (request.ProjectId <= 0) throw new ArgumentOutOfRangeException(nameof(request.ProjectId));
        if (request.ChannelId <= 0) throw new ArgumentOutOfRangeException(nameof(request.ChannelId));
        if (request.ReleaseId <= 0) throw new ArgumentOutOfRangeException(nameof(request.ReleaseId));
        if (request.EnvironmentId <= 0) throw new ArgumentOutOfRangeException(nameof(request.EnvironmentId));
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Deployment name is required.", nameof(request.Name));
        if (string.IsNullOrWhiteSpace(request.TaskName)) throw new ArgumentException("Task name is required.", nameof(request.TaskName));
    }
}
