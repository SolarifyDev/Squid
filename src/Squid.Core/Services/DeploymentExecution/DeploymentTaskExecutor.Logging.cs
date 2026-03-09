using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task CreateTaskActivityNodeAsync(CancellationToken ct)
    {
        var projectName = string.IsNullOrWhiteSpace(_ctx.Project?.Name) ? $"Project {_ctx.Deployment?.ProjectId}" : _ctx.Project.Name;
        var releaseVersion = string.IsNullOrWhiteSpace(_ctx.Release?.Version) ? "Unknown" : _ctx.Release.Version;
        var environmentName = string.IsNullOrWhiteSpace(_ctx.Environment?.Name) ? $"Environment {_ctx.Deployment?.EnvironmentId}" : _ctx.Environment.Name;

        _ctx.TaskActivityNode = await CreateActivityNodeAsync(null, $"Deploy {projectName} release {releaseVersion} to {environmentName}", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, ct).ConfigureAwait(false);

        await LogInfoAsync($"Deploying {projectName} release {releaseVersion} to {environmentName}", "System", ct).ConfigureAwait(false);
    }

    private async Task RecordSuccessAsync(CancellationToken ct)
    {
        await LogInfoAsync("Deployment completed successfully", "System", ct).ConfigureAwait(false);
        await RecordCompletionAsync(true, "Deployment completed successfully");
        await UpdateActivityNodeStatusAsync(_ctx.TaskActivityNode, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);

        await _genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await _serverTaskService.TransitionStateAsync(_ctx.ServerTaskId, TaskState.Executing, TaskState.Success, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await TriggerAutoDeploymentsAsync(ct).ConfigureAwait(false);

        Log.Information("Task {TaskId} completed successfully", _ctx.ServerTaskId);
    }

    private async Task TriggerAutoDeploymentsAsync(CancellationToken ct)
    {
        try
        {
            if (_ctx.Deployment == null) return;

            await _autoDeployService.TriggerAutoDeploymentsAsync(_ctx.Deployment.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-deploy trigger failed for deployment {DeploymentId}, continuing", _ctx.Deployment?.Id);
        }
    }

    private async Task RecordFailureAsync(Exception ex, CancellationToken ct)
    {
        Log.Error(ex, "Task {TaskId} failed: {ErrorMessage}", _ctx.ServerTaskId, ex.Message);

        await UpdateActivityNodeStatusAsync(_ctx.TaskActivityNode, DeploymentActivityLogNodeStatus.Failed, ct).ConfigureAwait(false);
        await LogErrorAsync(ex.Message, "System", ct).ConfigureAwait(false);

        if (_ctx.Deployment != null)
            await RecordCompletionAsync(false, ex.Message);

        await _genericDataProvider.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await _serverTaskService.TransitionStateAsync(_ctx.ServerTaskId, TaskState.Executing, TaskState.Failed, cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task RecordCompletionAsync(bool success, string message)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(_ctx.Deployment.Id).ConfigureAwait(false);

        var completion = new Persistence.Entities.Deployments.DeploymentCompletion
        {
            DeploymentId = _ctx.Deployment.Id,
            CompletedTime = DateTimeOffset.UtcNow,
            State = success ? TaskState.Success : TaskState.Failed,
            SpaceId = deployment?.SpaceId ?? 1,
            SequenceNumber = 0
        };

        await _deploymentCompletionDataProvider.AddDeploymentCompletionAsync(completion).ConfigureAwait(false);

        Log.Information("Recorded deployment completion for deployment {DeploymentId}: {Status}", _ctx.Deployment.Id, success ? "Success" : "Failed");
    }

    // --- Convenience logging methods (3 params + optional nodeId) ---

    private Task LogInfoAsync(string message, string source, CancellationToken ct, long? nodeId = null)
        => PersistTaskLogAsync(ServerTaskLogCategory.Info, message, source, nodeId ?? _ctx.TaskActivityNode?.Id, ct);

    private Task LogWarningAsync(string message, string source, CancellationToken ct, long? nodeId = null)
        => PersistTaskLogAsync(ServerTaskLogCategory.Warning, message, source, nodeId ?? _ctx.TaskActivityNode?.Id, ct);

    private Task LogErrorAsync(string message, string source, CancellationToken ct, long? nodeId = null)
        => PersistTaskLogAsync(ServerTaskLogCategory.Error, message, source, nodeId ?? _ctx.TaskActivityNode?.Id, ct);

    // --- Internal persistence methods ---

    private async Task<Persistence.Entities.Deployments.ActivityLog> CreateActivityNodeAsync(long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct)
    {
        try
        {
            return await _serverTaskService.AddActivityNodeAsync(_ctx.ServerTaskId, parentId, name, nodeType, status, sortOrder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create activity log node: {Name}", name);
            return null;
        }
    }

    private async Task PersistTaskLogAsync(ServerTaskLogCategory category, string message, string source, long? activityNodeId, CancellationToken ct)
    {
        try
        {
            var seq = _ctx.NextLogSequence();
            await _serverTaskService.AddLogAsync(_ctx.ServerTaskId, seq, category, message, source, activityNodeId, DateTimeOffset.UtcNow, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist task log entry");
        }
    }

    private async Task PersistScriptOutputAsync(ScriptExecutionResult execResult, string source, long? activityNodeId, CancellationToken ct)
    {
        if (execResult.LogLines == null || execResult.LogLines.Count == 0) return;

        var stderrSet = execResult.StderrLines?.Count > 0 ? new HashSet<string>(execResult.StderrLines, StringComparer.Ordinal) : null;

        try
        {
            var entries = execResult.LogLines.Select(line => new ServerTaskLogWriteEntry
            {
                Category = stderrSet != null && stderrSet.Contains(line) ? ServerTaskLogCategory.Error : ServerTaskLogCategory.Info,
                MessageText = line,
                Source = source,
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = _ctx.NextLogSequence(),
                ActivityNodeId = activityNodeId
            }).ToList();

            await _serverTaskService.AddLogsAsync(_ctx.ServerTaskId, entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist script output for task {TaskId}", _ctx.ServerTaskId);
        }
    }

    private Task UpdateActivityNodeStatusAsync(Persistence.Entities.Deployments.ActivityLog node, DeploymentActivityLogNodeStatus status, CancellationToken ct)
    {
        if (node == null) return Task.CompletedTask;

        return _serverTaskService.UpdateActivityNodeStatusAsync(node.Id, status, DateTimeOffset.UtcNow, ct);
    }
}
