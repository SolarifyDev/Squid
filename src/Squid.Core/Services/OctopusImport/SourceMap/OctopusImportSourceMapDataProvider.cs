using Npgsql;
using NpgsqlTypes;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SourceMap;

public sealed class OctopusImportSourceMapDataProvider(IRepository repository) : IOctopusImportSourceMapDataProvider
{
    // First key of the two-key advisory lock ("SQOI"): keeps these locks apart from every other advisory lock user.
    public const int AdvisoryLockNamespace = 0x53514F49;

    public const int UpsertBatchSize = 500;

    private const int ParametersPerRow = 8;

    public async Task<OctopusImportPriorMappings> LoadAsync(int spaceId, CancellationToken ct = default)
    {
        var rows = await repository.FromSqlRaw<OctopusImportSourceMap>(BuildLiveRowsSql(), spaceId).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        return ToPriorMappings(rows);
    }

    public async Task UpsertAsync(IReadOnlyList<OctopusImportSourceMapRow> rows, Guid sessionId, CancellationToken ct = default)
    {
        foreach (var batch in DistinctByKey(rows).Chunk(UpsertBatchSize))
            await UpsertBatchAsync(batch, sessionId, ct).ConfigureAwait(false);
    }

    public async Task AcquireSpaceLockAsync(int spaceId, CancellationToken ct = default)
    {
        if (repository.Database.CurrentTransaction == null)
            throw new InvalidOperationException("The Octopus import space lock must be taken inside the import transaction; outside one it would be released immediately.");

        var key = $"octopus-import:{spaceId}";

        await repository.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, hashtext({key}))", ct).ConfigureAwait(false);
    }

    // One EXISTS-filtered SELECT per known type, combined with UNION ALL: a row survives only while its destination row exists.
    private static string BuildLiveRowsSql()
        => string.Join("\nUNION ALL\n", OctopusImportSourceMapTables.ByKind.Select(pair => $"SELECT m.* FROM octopus_import_source_map m WHERE m.space_id = {{0}} AND m.source_type = '{pair.Key}' AND EXISTS (SELECT 1 FROM {pair.Value} d WHERE d.id = m.destination_id)"));

    private static OctopusImportPriorMappings ToPriorMappings(IReadOnlyList<OctopusImportSourceMap> rows)
    {
        var mappings = rows.ToDictionary(r => OctopusImportPriorMappings.Key(r.SourceType, r.SourceId), r => new OctopusImportPriorMapping(r.DestinationId, r.OutcomeState, r.Fingerprint, r.DetailFingerprint));

        return new OctopusImportPriorMappings(mappings, LatestInstallationId(rows));
    }

    private static string LatestInstallationId(IEnumerable<OctopusImportSourceMap> rows)
        => rows.Where(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType).OrderByDescending(r => r.LastModifiedDate).ThenByDescending(r => r.Id).Select(r => r.SourceId).FirstOrDefault();

    // ON CONFLICT DO UPDATE cannot touch the same row twice in one statement, so the last row per key wins up front.
    private static IEnumerable<OctopusImportSourceMapRow> DistinctByKey(IReadOnlyList<OctopusImportSourceMapRow> rows)
    {
        var latest = new Dictionary<(int, string, string), OctopusImportSourceMapRow>();

        foreach (var row in rows ?? [])
        {
            EnsureValid(row);
            latest[(row.SpaceId, row.SourceType.Trim(), OctopusImportPriorMappings.Normalize(row.SourceId))] = row;
        }

        return latest.Values;
    }

    private static void EnsureValid(OctopusImportSourceMapRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.SourceType) || string.IsNullOrWhiteSpace(row.SourceId))
            throw new ArgumentException("Octopus import source-map rows require a source type and a source id.", nameof(row));

        if (row.DestinationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(row), row.DestinationId, $"Octopus import source-map row '{row.SourceType}:{row.SourceId}' requires a positive destination id.");

        if (row.OutcomeState is not (OctopusImportSourceMapQueries.CreatedOutcome or OctopusImportSourceMapQueries.ReusedOutcome))
            throw new ArgumentOutOfRangeException(nameof(row), row.OutcomeState, $"Octopus import source-map row '{row.SourceType}:{row.SourceId}' must be Created or Reused.");
    }

    private async Task UpsertBatchAsync(IReadOnlyList<OctopusImportSourceMapRow> batch, Guid sessionId, CancellationToken ct)
    {
        var parameters = batch.SelectMany((row, index) => RowParameters(row, index, sessionId)).ToArray();

        await repository.Database.ExecuteSqlRawAsync(BuildUpsertSql(batch.Count), parameters, ct).ConfigureAwait(false);
    }

    private static IEnumerable<NpgsqlParameter> RowParameters(OctopusImportSourceMapRow row, int index, Guid sessionId)
    {
        var offset = index * ParametersPerRow;

        yield return Parameter(offset, NpgsqlDbType.Integer, row.SpaceId);
        yield return Parameter(offset + 1, NpgsqlDbType.Varchar, row.SourceType.Trim());
        yield return Parameter(offset + 2, NpgsqlDbType.Varchar, OctopusImportPriorMappings.Normalize(row.SourceId));
        yield return Parameter(offset + 3, NpgsqlDbType.Integer, row.DestinationId);
        yield return Parameter(offset + 4, NpgsqlDbType.Varchar, row.OutcomeState);
        yield return Parameter(offset + 5, NpgsqlDbType.Varchar, row.Fingerprint);
        yield return Parameter(offset + 6, NpgsqlDbType.Varchar, row.DetailFingerprint);
        yield return Parameter(offset + 7, NpgsqlDbType.Uuid, sessionId);
    }

    private static NpgsqlParameter Parameter(int ordinal, NpgsqlDbType type, object value)
        => new($"p{ordinal}", type) { Value = value ?? DBNull.Value };

    private static string BuildUpsertSql(int rowCount)
    {
        var values = string.Join(", ", Enumerable.Range(0, rowCount).Select(ValuesTuple));

        return $"""
            INSERT INTO octopus_import_source_map AS m (space_id, source_type, source_id, destination_id, outcome_state, fingerprint, detail_fingerprint, last_session_id)
            VALUES {values}
            ON CONFLICT (space_id, source_type, source_id) DO UPDATE SET
                outcome_state = CASE WHEN m.outcome_state = '{OctopusImportSourceMapQueries.CreatedOutcome}' AND m.destination_id = EXCLUDED.destination_id THEN m.outcome_state ELSE EXCLUDED.outcome_state END,
                fingerprint = CASE WHEN m.destination_id = EXCLUDED.destination_id THEN COALESCE(EXCLUDED.fingerprint, m.fingerprint) ELSE EXCLUDED.fingerprint END,
                detail_fingerprint = CASE WHEN m.destination_id = EXCLUDED.destination_id THEN COALESCE(EXCLUDED.detail_fingerprint, m.detail_fingerprint) ELSE EXCLUDED.detail_fingerprint END,
                destination_id = EXCLUDED.destination_id,
                last_session_id = EXCLUDED.last_session_id,
                last_modified_date = now()
            """;
    }

    private static string ValuesTuple(int index)
    {
        var offset = index * ParametersPerRow;

        return "(" + string.Join(", ", Enumerable.Range(offset, ParametersPerRow).Select(ordinal => $"@p{ordinal}")) + ")";
    }
}
