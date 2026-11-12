using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;

namespace Squid.Core.Services.OctopusImport;

/// <summary>Scalars of the history unit: server task, deployment and completion.</summary>
public sealed partial class OctopusImportDeploymentHistoryImporter
{
    private const int TaskNameMaxLength = 50;
    private const int DeploymentNameMaxLength = 200;
    private const string DetailsJsonSection = "Details";
    private const string PendingInterruptionsJsonProperty = "PendingInterruptions";

    private sealed class HistoryUnitContext
    {
        // Unit
        public OctopusImportHistoryUnit Unit { get; init; }
        public bool IsNewTask => Unit.ExistingTaskId == null;
        public bool WritesScalars => IsNewTask || Unit.ExistingDeploymentId == null || Unit.ScalarChanged;
        public bool WritesChildren => IsNewTask || Unit.ReplaceChildren;
        public bool WritesDetails => WritesChildren && Unit.Details != null;
        public bool RefreshesKeptChildren => WritesScalars && !WritesChildren;

        // Task
        public int ActorId { get; init; }
        public string Username { get; init; }
        public OctopusImportTaskState State { get; init; }
        public DateTimeOffset Created { get; init; }
        public DateTimeOffset QueueTime { get; init; }
        public DateTimeOffset? StartTime { get; init; }
        public DateTimeOffset? CompletedTime { get; init; }
        public DateTimeOffset LastActivityTime { get; init; }
        public int DurationSeconds { get; init; }
    }

    private async Task<HistoryUnitContext> BuildContextAsync(OctopusImportHistoryUnit unit, CancellationToken ct)
    {
        var task = unit.Task;
        var created = Utc(unit.Deployment.Created ?? task.QueueTime ?? task.StartTime ?? DateTimeOffset.UtcNow);
        var queueTime = Utc(task.QueueTime ?? created);
        var startTime = task.StartTime?.ToUniversalTime();
        var completedTime = task.CompletedTime?.ToUniversalTime();

        return new HistoryUnitContext
        {
            Unit = unit,
            ActorId = await ResolveActorIdAsync(unit.Deployment.DeployedBy, ct).ConfigureAwait(false),
            Username = string.IsNullOrWhiteSpace(unit.Deployment.DeployedBy) ? CurrentUsers.InternalUser.Name : unit.Deployment.DeployedBy.Trim(),
            State = OctopusImportTaskStateNormalizer.Normalize(task.State),
            Created = created,
            QueueTime = queueTime,
            StartTime = startTime,
            CompletedTime = completedTime,
            LastActivityTime = completedTime ?? startTime ?? queueTime,
            DurationSeconds = DurationSeconds(task, startTime, completedTime)
        };
    }

    private async Task<int> ResolveActorIdAsync(string deployedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deployedBy)) return CurrentUsers.InternalUser.Id;

        var normalized = deployedBy.Trim().ToUpperInvariant();
        var user = await _repository.QueryNoTracking<UserAccount>(candidate => candidate.NormalizedUserName == normalized && !candidate.IsDisabled).Select(candidate => (int?)candidate.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return user is > 0 ? user.Value : CurrentUsers.InternalUser.Id;
    }

    // The REST task resource carries no DurationSeconds, so it is derived from the times when absent.
    private static int DurationSeconds(OctopusServerTaskDto task, DateTimeOffset? startTime, DateTimeOffset? completedTime)
    {
        if (task.DurationSeconds > 0) return task.DurationSeconds;

        return startTime != null && completedTime != null ? Math.Max(0, (int)(completedTime.Value - startTime.Value).TotalSeconds) : 0;
    }

    private async Task<int> WriteServerTaskAsync(CancellationToken ct)
    {
        if (_ctx.IsNewTask) return await InsertServerTaskAsync(ct).ConfigureAwait(false);

        var taskId = _ctx.Unit.ExistingTaskId!.Value;

        if (_ctx.WritesScalars || _ctx.WritesChildren) await UpdateServerTaskAsync(taskId, ct).ConfigureAwait(false);

        return taskId;
    }

    private async Task<int> InsertServerTaskAsync(CancellationToken ct)
    {
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
                 {14}, {15}, 'Deploy',
                 NULL, {2}, {16}, 0, 0,
                 {17}, NULL, false, {18}, {19},
                 {19})
            RETURNING id
            """;

        var json = await BuildTaskJsonAsync(ct).ConfigureAwait(false);

        return await InsertReturningIdAsync(sql, ServerTaskValues(json).Concat([_ctx.Created, _ctx.ActorId]).ToArray(), ct).ConfigureAwait(false);
    }

    private async Task UpdateServerTaskAsync(int taskId, CancellationToken ct)
    {
        const string sql = """
            UPDATE server_task SET
                name = {0}, description = {1}, queue_time = {2}, start_time = {3}, completed_time = {4}, error_message = {5},
                concurrency_tag = NULL, state = {6}, has_warnings_or_errors = {7}, server_node_id = {8},
                project_id = {9}, environment_id = {10}, duration_seconds = {11}, data_version = {12}, space_id = {13},
                last_modified_date = {14}, business_process_state = {15}, priority_time = {2}, state_order = {16},
                "json" = {17}, has_pending_interruptions = false, last_modified_by = {18}
            WHERE id = {19}
            """;

        var json = await BuildTaskJsonAsync(ct).ConfigureAwait(false);

        await _dbContext.Database.ExecuteSqlRawAsync(sql, OctopusImportText.StripNul(ServerTaskValues(json).Concat([_ctx.ActorId, taskId])), ct).ConfigureAwait(false);
    }

    private object[] ServerTaskValues(string json)
    {
        var task = _ctx.Unit.Task;

        return [Truncate(task.Name.Trim(), TaskNameMaxLength), task.Description ?? string.Empty, _ctx.QueueTime, _ctx.StartTime, _ctx.CompletedTime, task.ErrorMessage ?? string.Empty,
            _ctx.State.State, task.HasWarningsOrErrors, Guid.Empty, _ctx.Unit.ProjectId, _ctx.Unit.EnvironmentId, _ctx.DurationSeconds, Guid.NewGuid().ToByteArray(), _ctx.Unit.SpaceId,
            _ctx.LastActivityTime, _ctx.State.BusinessProcessState, _ctx.State.StateOrder, json];
    }

    // The details section (artifacts, pending interruptions) is rewritten only with the details; otherwise the stored one is kept.
    private async Task<string> BuildTaskJsonAsync(CancellationToken ct)
    {
        var task = _ctx.Unit.Task;
        var root = new JsonObject
        {
            ["Octopus"] = new JsonObject { ["DeployedById"] = _ctx.Unit.Deployment.DeployedById ?? string.Empty, ["DeployedBy"] = _ctx.Unit.Deployment.DeployedBy ?? string.Empty, ["TaskState"] = task.State, ["Name"] = task.Name },
            ["QueueTime"] = _ctx.QueueTime, ["StartTime"] = _ctx.StartTime, ["CompletedTime"] = _ctx.CompletedTime, ["DurationSeconds"] = _ctx.DurationSeconds
        };

        var details = _ctx.WritesDetails ? DetailsSection(_ctx.Unit.Details) : await LoadStoredDetailsSectionAsync(ct).ConfigureAwait(false);

        if (details != null) root["Octopus"]![DetailsJsonSection] = details;

        return root.ToJsonString();
    }

    private static JsonNode DetailsSection(OctopusTaskHistoryDetails details)
        => JsonSerializer.SerializeToNode(new { Artifacts = OctopusImportHistoryChildMapper.Artifacts(details), PendingInterruptions = OctopusImportHistoryChildMapper.PendingInterruptions(details) });

    private async Task<JsonNode> LoadStoredDetailsSectionAsync(CancellationToken ct)
    {
        if (_ctx.IsNewTask) return null;

        var taskId = _ctx.Unit.ExistingTaskId!.Value;
        var stored = await _repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => t.Id == taskId).Select(t => t.JSON).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return DropFinishedPendingInterruptions(ReadDetailsSection(stored));
    }

    // A finished task waits on nothing: interruptions the kept details still list as pending are dropped with it.
    private JsonNode DropFinishedPendingInterruptions(JsonNode details)
    {
        if (!_ctx.State.IsTerminal || details?[PendingInterruptionsJsonProperty] is not JsonArray interruptions) return details;

        foreach (var pending in interruptions.Where(IsPending).ToList())
            interruptions.Remove(pending);

        return details;
    }

    private static bool IsPending(JsonNode interruption)
        => interruption?["IsPending"] is JsonValue value && value.TryGetValue<bool>(out var isPending) && isPending;

    private static JsonNode ReadDetailsSection(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json)?["Octopus"]?[DetailsJsonSection]?.DeepClone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> WriteDeploymentAsync(int taskId, CancellationToken ct)
    {
        if (_ctx.Unit.ExistingDeploymentId == null) return await InsertDeploymentAsync(taskId, ct).ConfigureAwait(false);

        var deploymentId = _ctx.Unit.ExistingDeploymentId.Value;

        await UpdateDeploymentAsync(deploymentId, taskId, ct).ConfigureAwait(false);

        return deploymentId;
    }

    private Task<int> InsertDeploymentAsync(int taskId, CancellationToken ct)
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

        return InsertReturningIdAsync(sql, DeploymentValues(taskId).Append(_ctx.Created).ToArray(), ct);
    }

    private async Task UpdateDeploymentAsync(int deploymentId, int taskId, CancellationToken ct)
    {
        const string sql = """
            UPDATE deployment SET
                name = {0}, task_id = {1}, space_id = {2}, channel_id = {3}, project_id = {4}, release_id = {5},
                environment_id = {6}, "json" = {7}, deployed_by = {8}, deployed_to_machine_ids = {9},
                last_modified_date = now(), last_modified_by = {8}
            WHERE id = {10}
            """;

        await _dbContext.Database.ExecuteSqlRawAsync(sql, OctopusImportText.StripNul(DeploymentValues(taskId).Append(deploymentId)), ct).ConfigureAwait(false);
    }

    private object[] DeploymentValues(int taskId)
    {
        var unit = _ctx.Unit;
        var name = Truncate((unit.Deployment.Name ?? unit.Task.Name).Trim(), DeploymentNameMaxLength);

        return [name, taskId, unit.SpaceId, unit.ChannelId, unit.ProjectId, unit.ReleaseId, unit.EnvironmentId, BuildDeploymentJson(), _ctx.ActorId, JsonSerializer.Serialize(unit.Deployment.DeployedToMachineIds ?? [])];
    }

    // Machines are never imported: the deployment keeps the raw Octopus ids, and their names for display.
    private string BuildDeploymentJson()
    {
        var deployment = _ctx.Unit.Deployment;
        var names = OctopusImportDeploymentMachineNames.Resolve(deployment, _ctx.Unit.MachineNames);
        var machines = (deployment.DeployedToMachineIds ?? []).Select((id, index) => new { Id = id, Name = names[index] }).ToList();

        return JsonSerializer.Serialize(new { Octopus = new { DeployedById = deployment.DeployedById ?? string.Empty, DeployedBy = deployment.DeployedBy ?? string.Empty, Machines = machines } });
    }

    // Completions drive progression, the dashboard and rollback, so they follow the task state exactly like a normal deploy.
    private async Task WriteCompletionAsync(int deploymentId, CancellationToken ct)
    {
        if (!_ctx.WritesScalars) return;

        await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM deployment_completion WHERE deployment_id = {0}", new object[] { deploymentId }, ct).ConfigureAwait(false);

        if (_ctx.State.CompletionState == null) return;

        const string sql = """
            INSERT INTO deployment_completion (deployment_id, state, completed_time, space_id, created_date, created_by, last_modified_date, last_modified_by)
            VALUES ({0}, {1}, {2}, {3}, {2}, {4}, {2}, {4})
            """;

        await _dbContext.Database.ExecuteSqlRawAsync(sql, new object[] { deploymentId, _ctx.State.CompletionState, _ctx.LastActivityTime, _ctx.Unit.SpaceId, _ctx.ActorId }, ct).ConfigureAwait(false);
    }

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
