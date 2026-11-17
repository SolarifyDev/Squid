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

    private const int ParametersPerRow = 10;

    public Task<OctopusImportPriorMappings> LoadAsync(int spaceId, CancellationToken ct = default) => LoadKindsAsync(spaceId, OctopusImportSourceMapTables.ByKind.Keys.ToList(), ct);

    public async Task<OctopusImportPriorMappings> LoadKindsAsync(int spaceId, IReadOnlyCollection<string> sourceTypes, CancellationToken ct = default)
    {
        var tables = OctopusImportSourceMapTables.ByKind.Where(pair => sourceTypes?.Contains(pair.Key) == true).ToList();

        if (tables.Count == 0) return OctopusImportPriorMappings.Empty;

        var rows = await repository.FromSqlRaw<OctopusImportSourceMap>(BuildLiveRowsSql(tables), spaceId).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var recordedChildKeys = await LoadRecordedChildKeysAsync(spaceId, tables.Select(pair => pair.Key), ct).ConfigureAwait(false);

        return ToPriorMappings(rows, recordedChildKeys);
    }

    // Every recorded row of the requested choosable child kinds, live or not: the prior mappings name the ones whose destination is gone.
    private async Task<HashSet<(string Type, string Id)>> LoadRecordedChildKeysAsync(int spaceId, IEnumerable<string> sourceTypes, CancellationToken ct)
    {
        var types = sourceTypes.Where(OctopusImportSourceMapQueries.ChoosableChildSourceTypes.Contains).ToList();

        if (types.Count == 0) return [];

        var rows = await repository.QueryNoTracking<OctopusImportSourceMap>(m => m.SpaceId == spaceId && types.Contains(m.SourceType)).Select(m => new { m.SourceType, m.SourceId }).ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => (r.SourceType, r.SourceId)).ToHashSet();
    }

    public async Task<List<OctopusImportSourceMapRow>> GetByDestinationsAsync(int spaceId, IReadOnlyCollection<string> sourceTypes, IReadOnlyCollection<int> destinationIds, CancellationToken ct = default)
    {
        if (sourceTypes is not { Count: > 0 } || destinationIds is not { Count: > 0 }) return [];

        var types = sourceTypes.Distinct().ToList();
        var ids = destinationIds.Distinct().ToList();

        return await repository.QueryNoTracking<OctopusImportSourceMap>(m => m.SpaceId == spaceId && types.Contains(m.SourceType) && ids.Contains(m.DestinationId)).Select(m => new OctopusImportSourceMapRow(m.SpaceId, m.SourceType, m.SourceId, m.DestinationId, m.OutcomeState, null, null)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(IReadOnlyList<OctopusImportSourceMapRow> rows, Guid sessionId, CancellationToken ct = default)
    {
        var distinct = DistinctByKey(rows).ToList();

        foreach (var batch in distinct.Chunk(UpsertBatchSize))
            await UpsertBatchAsync(batch, sessionId, ct).ConfigureAwait(false);

        foreach (var row in distinct.Where(r => r.DetailsCleared))
            await ClearDetailFingerprintAsync(row, ct).ConfigureAwait(false);
    }

    public async Task SupersedeMissingSecretsAsync(int spaceId, string sourceType, IReadOnlyCollection<string> sourceIds, CancellationToken ct = default)
    {
        if (sourceIds is not { Count: > 0 }) return;

        var ids = sourceIds.Select(OctopusImportPriorMappings.Normalize).Distinct().ToArray();

        await repository.Database.ExecuteSqlInterpolatedAsync($"UPDATE octopus_import_source_map SET outcome_state = {OctopusImportSourceMapQueries.SupersededOutcome}, last_modified_date = now() WHERE space_id = {spaceId} AND source_type = {sourceType} AND source_id = ANY({ids}) AND outcome_state = {OctopusImportSourceMapQueries.MissingOutcome}", ct).ConfigureAwait(false);
    }

    public async Task AcquireSpaceLockAsync(int spaceId, CancellationToken ct = default)
    {
        if (repository.Database.CurrentTransaction == null)
            throw new InvalidOperationException("The Octopus import space lock must be taken inside the import transaction; outside one it would be released immediately.");

        var key = $"octopus-import:{spaceId}";

        await repository.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, hashtext({key}))", ct).ConfigureAwait(false);
    }

    // One EXISTS-filtered SELECT per known type, combined with UNION ALL: a row survives only while its destination row and that row's owners exist.
    private static string BuildLiveRowsSql(IEnumerable<KeyValuePair<string, string>> tables)
        => string.Join("\nUNION ALL\n", tables.Select(pair => $"SELECT m.* FROM octopus_import_source_map m WHERE m.space_id = {{0}} AND m.source_type = '{pair.Key}' AND EXISTS (SELECT 1 FROM {pair.Value} d WHERE d.id = m.destination_id{OwnerCondition(pair.Key)})"));

    private static string OwnerCondition(string sourceType)
        => OctopusImportSourceMapTables.OwnerPredicates.TryGetValue(sourceType, out var predicate) ? $" AND {predicate}" : string.Empty;

    private static OctopusImportPriorMappings ToPriorMappings(IReadOnlyList<OctopusImportSourceMap> rows, IReadOnlySet<(string Type, string Id)> recordedChildKeys)
    {
        var mappings = NewestPerKey(rows).ToDictionary(r => OctopusImportPriorMappings.Key(r.SourceType, r.SourceId), r => new OctopusImportPriorMapping(r.DestinationId, r.OutcomeState, r.Fingerprint, r.DetailFingerprint, r.CreatedInstallationId, r.CreatedExportSourceId));

        return new OctopusImportPriorMappings(mappings, LatestInstallationId(rows), ExportSourceIds(rows), recordedChildKeys);
    }

    // Two stored ids can share one canonical key only if a row escaped the canonicalising migration; the newest wins
    // (the migration's rule) instead of failing every preview of the space.
    private static IEnumerable<OctopusImportSourceMap> NewestPerKey(IEnumerable<OctopusImportSourceMap> rows)
        => rows.GroupBy(r => OctopusImportPriorMappings.Key(r.SourceType, r.SourceId)).Select(g => g.OrderByDescending(r => r.LastModifiedDate).ThenByDescending(r => r.Id).First());

    private static List<string> ExportSourceIds(IEnumerable<OctopusImportSourceMap> rows)
        => rows.Where(r => r.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType).OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).Select(r => r.SourceId).ToList();

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

        if (OctopusImportSourceMapQueries.SecretSourceTypes.Contains(row.SourceType.Trim()))
        {
            if (!OctopusImportSourceMapQueries.SecretOutcomes.Contains(row.OutcomeState ?? string.Empty))
                throw new ArgumentOutOfRangeException(nameof(row), row.OutcomeState, $"Octopus import secret row '{row.SourceType}:{row.SourceId}' must be Missing, Filled or Superseded.");

            return;
        }

        if (row.OutcomeState is not (OctopusImportSourceMapQueries.CreatedOutcome or OctopusImportSourceMapQueries.ReusedOutcome))
            throw new ArgumentOutOfRangeException(nameof(row), row.OutcomeState, $"Octopus import source-map row '{row.SourceType}:{row.SourceId}' must be Created or Reused.");
    }

    private async Task UpsertBatchAsync(IReadOnlyList<OctopusImportSourceMapRow> batch, Guid sessionId, CancellationToken ct)
    {
        var parameters = batch.SelectMany((row, index) => RowParameters(row, index, sessionId)).ToArray();

        await repository.Database.ExecuteSqlRawAsync(BuildUpsertSql(batch.Count), parameters, ct).ConfigureAwait(false);
    }

    // The upsert keeps a stored detail fingerprint when the incoming one is null, so "the details are gone" is its own write.
    private async Task ClearDetailFingerprintAsync(OctopusImportSourceMapRow row, CancellationToken ct)
    {
        var sourceType = row.SourceType.Trim();
        var sourceId = OctopusImportPriorMappings.Normalize(row.SourceId);

        await repository.Database.ExecuteSqlInterpolatedAsync($"UPDATE octopus_import_source_map SET detail_fingerprint = NULL WHERE space_id = {row.SpaceId} AND source_type = {sourceType} AND source_id = {sourceId}", ct).ConfigureAwait(false);
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
        yield return Parameter(offset + 8, NpgsqlDbType.Varchar, OriginId(row.CreatedInstallationId));
        yield return Parameter(offset + 9, NpgsqlDbType.Varchar, OriginId(row.CreatedExportSourceId));
    }

    private static string OriginId(string id) => string.IsNullOrWhiteSpace(id) ? null : OctopusImportPriorMappings.Normalize(id);

    private static NpgsqlParameter Parameter(int ordinal, NpgsqlDbType type, object value)
        => new($"p{ordinal}", type) { Value = value ?? DBNull.Value };

    private static string BuildUpsertSql(int rowCount)
    {
        var values = string.Join(", ", Enumerable.Range(0, rowCount).Select(ValuesTuple));

        return $"""
            INSERT INTO octopus_import_source_map AS m (space_id, source_type, source_id, destination_id, outcome_state, fingerprint, detail_fingerprint, last_session_id, created_installation_id, created_export_source_id)
            VALUES {values}
            ON CONFLICT (space_id, source_type, source_id) DO UPDATE SET
                outcome_state = CASE WHEN m.outcome_state IN ({StickyOutcomesSql()}) AND m.destination_id = EXCLUDED.destination_id THEN m.outcome_state ELSE EXCLUDED.outcome_state END,
                fingerprint = CASE WHEN m.destination_id = EXCLUDED.destination_id THEN COALESCE(EXCLUDED.fingerprint, m.fingerprint) ELSE EXCLUDED.fingerprint END,
                detail_fingerprint = CASE WHEN m.destination_id = EXCLUDED.destination_id THEN COALESCE(EXCLUDED.detail_fingerprint, m.detail_fingerprint) ELSE EXCLUDED.detail_fingerprint END,
                created_installation_id = CASE WHEN m.destination_id = EXCLUDED.destination_id THEN m.created_installation_id ELSE EXCLUDED.created_installation_id END,
                created_export_source_id = CASE WHEN m.destination_id = EXCLUDED.destination_id THEN m.created_export_source_id ELSE EXCLUDED.created_export_source_id END,
                destination_id = EXCLUDED.destination_id,
                last_session_id = EXCLUDED.last_session_id,
                last_modified_date = now()
            """;
    }

    // Constants only (never user input), so safe to splice into SQL.
    private static string StickyOutcomesSql() => string.Join(", ", OctopusImportSourceMapQueries.StickyOutcomes.Select(o => $"'{o}'"));

    private static string ValuesTuple(int index)
    {
        var offset = index * ParametersPerRow;

        return "(" + string.Join(", ", Enumerable.Range(offset, ParametersPerRow).Select(ordinal => $"@p{ordinal}")) + ")";
    }
}
