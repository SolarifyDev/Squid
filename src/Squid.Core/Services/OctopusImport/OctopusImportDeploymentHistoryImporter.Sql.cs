using Microsoft.EntityFrameworkCore;
using Squid.Core.Services.OctopusImport.History;
using Squid.Message.Enums.Events;

namespace Squid.Core.Services.OctopusImport;

/// <summary>Batched multi-row INSERT helpers. A batch stays far below PostgreSQL's 65535 bind-parameter limit.</summary>
public sealed partial class OctopusImportDeploymentHistoryImporter
{
    public const int InsertBatchRows = 500;

    // Ids are reserved up front so a whole tree (children reference their parent's id) is written in batches.
    private async Task<IReadOnlyList<long>> ReserveActivityIdsAsync(int count, CancellationToken ct)
        => await _dbContext.Database.SqlQueryRaw<long>("SELECT nextval('activity_log_id_seq') AS \"Value\" FROM generate_series(1, {0})", count).ToListAsync(ct).ConfigureAwait(false);

    private Task InsertActivityNodesAsync(int taskId, IReadOnlyList<OctopusImportActivityNode> nodes, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var rows = nodes.Select(n => new object[] { ids[n.Index], taskId, n.ParentIndex is { } parent ? ids[parent] : (long?)null, n.Name, (int)n.NodeType, (int)n.Status, n.StartedAt.ToUniversalTime(), n.EndedAt?.ToUniversalTime(), n.SortOrder, _ctx.ActorId, _ctx.ActorId }).ToList();

        return InsertRowsAsync("activity_log (id, server_task_id, parent_id, name, node_type, status, started_at, ended_at, sort_order, created_by, last_modified_by)", rows, null, ct);
    }

    private Task InsertLogLinesAsync(int taskId, IReadOnlyList<OctopusImportLogLine> lines, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var rows = lines.Select(l => new object[] { taskId, (int)l.Category, l.MessageText ?? string.Empty, l.Detail, ImportSource, l.OccurredAt.ToUniversalTime(), l.SequenceNumber, ids[l.NodeIndex], l.OccurredAt.ToUniversalTime(), _ctx.ActorId, _ctx.ActorId }).ToList();

        return InsertRowsAsync("server_task_log (server_task_id, category, message_text, detail, source, occurred_at, sequence_number, activity_node_id, created_date, created_by, last_modified_by)", rows, null, ct);
    }

    private Task InsertInterruptionsAsync(int deploymentId, int taskId, IReadOnlyList<OctopusImportInterruptionRow> interruptions, CancellationToken ct)
    {
        var rows = interruptions.Select(i => new object[] { taskId, deploymentId, i.Title, i.Resolution, i.CreatedAt.ToUniversalTime(), i.ResolvedAt.ToUniversalTime(), _ctx.Unit.SpaceId, (int)i.Type, i.SubmittedValuesJson, i.ResponsibleTeamIds, _ctx.ActorId, _ctx.ActorId }).ToList();

        return InsertRowsAsync("deployment_interruption (server_task_id, deployment_id, step_name, resolution, created_date, resolved_at, space_id, interruption_type, submitted_values_json, responsible_team_ids, created_by, last_modified_by)", rows, null, ct);
    }

    private Task InsertEventsAsync(int deploymentId, int taskId, IReadOnlyList<OctopusImportEventRow> events, EventReferenceNames names, CancellationToken ct)
    {
        var unit = _ctx.Unit;
        var rows = events.Select(e => new object[] { (short)e.Category, EventReferences(e, names), unit.SpaceId, unit.ProjectId, unit.ReleaseId, deploymentId, unit.EnvironmentId, taskId, EventUserId(e), e.Username, (short)EventIdentityEstablishedWith.Server, e.Occurred.ToUniversalTime() }).ToList();

        return InsertRowsAsync("event (category, references_json, space_id, project_id, release_id, deployment_id, environment_id, server_task_id, user_id, username, established_with, occurred)", rows, new Dictionary<int, string> { [1] = "jsonb" }, ct);
    }

    private async Task<int> InsertReturningIdAsync(string sql, object[] values, CancellationToken ct)
    {
        var result = await _dbContext.Database.SqlQueryRaw<int>(sql, OctopusImportText.StripNul(values)).ToListAsync(ct).ConfigureAwait(false);

        return result.Single();
    }

    private async Task InsertRowsAsync(string target, IReadOnlyList<object[]> rows, IReadOnlyDictionary<int, string> casts, CancellationToken ct)
    {
        foreach (var batch in rows.Chunk(InsertBatchRows))
        {
            var sql = $"INSERT INTO {target} VALUES {string.Join(", ", batch.Select((row, index) => ValuesTuple(index * row.Length, row.Length, casts)))}";

            await _dbContext.Database.ExecuteSqlRawAsync(sql, OctopusImportText.StripNul(batch.SelectMany(row => row)), ct).ConfigureAwait(false);
        }
    }

    private static string ValuesTuple(int offset, int columns, IReadOnlyDictionary<int, string> casts)
        => "(" + string.Join(", ", Enumerable.Range(0, columns).Select(column => Placeholder(offset + column, casts?.GetValueOrDefault(column)))) + ")";

    private static string Placeholder(int ordinal, string cast) => cast == null ? $"{{{ordinal}}}" : $"CAST({{{ordinal}}} AS {cast})";
}
