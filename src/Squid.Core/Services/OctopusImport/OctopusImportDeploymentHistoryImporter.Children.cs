using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.History;
using Squid.Message.Constants;
using Squid.Message.Enums.Events;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The task's children: activity tree, log lines, resolved interruptions and events. An imported task is
/// read-only, so it owns every child row: replacing them deletes by task id and writes the new set.
/// </summary>
public sealed partial class OctopusImportDeploymentHistoryImporter
{
    private static readonly string[] ChildTables = ["activity_log", "server_task_log", "deployment_interruption", "event"];

    private async Task WriteChildrenAsync(int deploymentId, int taskId, CancellationToken ct)
    {
        if (!_ctx.WritesChildren) return;

        if (!_ctx.IsNewTask) await DeleteChildrenAsync(taskId, ct).ConfigureAwait(false);

        await WriteActivityTreeAsync(taskId, ct).ConfigureAwait(false);
        await WriteInterruptionsAsync(deploymentId, taskId, ct).ConfigureAwait(false);
        await WriteEventsAsync(deploymentId, taskId, ct).ConfigureAwait(false);
    }

    private async Task DeleteChildrenAsync(int taskId, CancellationToken ct)
    {
        foreach (var table in ChildTables)
            await _dbContext.Database.ExecuteSqlRawAsync($"DELETE FROM {table} WHERE server_task_id = {{0}}", new object[] { taskId }, ct).ConfigureAwait(false);
    }

    private async Task WriteActivityTreeAsync(int taskId, CancellationToken ct)
    {
        var tree = OctopusImportActivityTreeMapper.Map(BuildActivityRoot(), _ctx.WritesDetails ? _ctx.Unit.Details : null, MaxLogLinesPerTask());
        var nodeIds = await ReserveActivityIdsAsync(tree.Nodes.Count, ct).ConfigureAwait(false);

        await InsertActivityNodesAsync(taskId, tree.Nodes, nodeIds, ct).ConfigureAwait(false);
        await InsertLogLinesAsync(taskId, tree.Lines, nodeIds, ct).ConfigureAwait(false);
    }

    private OctopusImportActivityRoot BuildActivityRoot()
    {
        var task = _ctx.Unit.Task;
        var name = string.IsNullOrWhiteSpace(task.Description) ? task.Name : task.Description;
        var message = $"Imported Octopus deployment {_ctx.State.State} for release to environment {_ctx.Unit.EnvironmentId}.";
        var detail = JsonSerializer.Serialize(new { Source = ImportSource, DeployedBy = _ctx.Unit.Deployment.DeployedBy ?? string.Empty, DeployedById = _ctx.Unit.Deployment.DeployedById ?? string.Empty, TaskState = task.State, ErrorMessage = task.ErrorMessage ?? string.Empty });

        return new OctopusImportActivityRoot(name, _ctx.State, _ctx.StartTime ?? _ctx.QueueTime, _ctx.State.IsTerminal ? _ctx.LastActivityTime : null, message, detail);
    }

    private async Task WriteInterruptionsAsync(int deploymentId, int taskId, CancellationToken ct)
    {
        var rows = OctopusImportHistoryChildMapper.MapResolvedInterruptions(_ctx.WritesDetails ? _ctx.Unit.Details : null, _ctx.Unit.TeamResolver);

        await InsertInterruptionsAsync(deploymentId, taskId, rows, ct).ConfigureAwait(false);
    }

    private async Task WriteEventsAsync(int deploymentId, int taskId, CancellationToken ct)
    {
        var events = OctopusImportHistoryChildMapper.MapEvents(_ctx.WritesDetails ? _ctx.Unit.Details.Events : null, _ctx.State, _ctx.LastActivityTime, _ctx.Username);

        if (events.Count == 0) return;

        var names = await LoadEventReferenceNamesAsync(ct).ConfigureAwait(false);

        await InsertEventsAsync(deploymentId, taskId, events, names, ct).ConfigureAwait(false);
    }

    private async Task<EventReferenceNames> LoadEventReferenceNamesAsync(CancellationToken ct)
    {
        var unit = _ctx.Unit;
        var release = await _repository.QueryNoTracking<Release>(r => r.Id == unit.ReleaseId).Select(r => r.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var environment = await _repository.QueryNoTracking<Persistence.Entities.Deployments.Environment>(e => e.Id == unit.EnvironmentId).Select(e => e.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var project = await _repository.QueryNoTracking<Project>(p => p.Id == unit.ProjectId).Select(p => p.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new EventReferenceNames(project, release, environment);
    }

    // An Octopus event without a Squid category keeps its own category and message under "Octopus".
    private static string EventReferences(OctopusImportEventRow row, EventReferenceNames names)
        => row.OctopusCategory == null || row.Category != EventCategory.ImportedOctopusEvent
            ? JsonSerializer.Serialize(new { names.Project, names.Release, names.Environment })
            : JsonSerializer.Serialize(new { names.Project, names.Release, names.Environment, Octopus = new { Category = row.OctopusCategory, row.Message } });

    // Only the deploying user is matched to a Squid user; anybody else in Octopus's events stays a name.
    private int? EventUserId(OctopusImportEventRow row)
        => _ctx.ActorId != CurrentUsers.InternalUser.Id && string.Equals(row.Username, _ctx.Username, StringComparison.OrdinalIgnoreCase) ? _ctx.ActorId : null;

    private sealed record EventReferenceNames(string Project, string Release, string Environment);
}
