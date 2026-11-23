using Npgsql;
using NpgsqlTypes;
using Squid.Core.Persistence.Db;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// <see cref="IDataStreamPersistence"/> on the <c>halibut_data_stream</c> tables. Every call opens its own pooled connection from the connection string of
/// <see cref="SquidDbContext"/> (never the shared configuration, which tests and other tools may point elsewhere), so it can be used from any thread and any
/// scope, and never touches a caller's context or transaction. A singleton: the connection string is read once, through a short-lived child scope.
/// </summary>
public sealed class PostgresDataStreamPersistence(ILifetimeScope scope) : IDataStreamPersistence, ISingletonDependency
{
    internal const int LongCommandSeconds = 600;

    private const string HeadersSql = "SELECT stream_id, length, chunk_count, protected_key FROM halibut_data_stream WHERE stream_id = ANY($1)";
    private const string ChunkSql = "SELECT content FROM halibut_data_stream_chunk WHERE stream_id = $1 AND chunk_index = $2";
    private const string DeleteSql = "DELETE FROM halibut_data_stream WHERE stream_id = $1";
    private const string DeleteOlderSql = "DELETE FROM halibut_data_stream WHERE created_date < now() - make_interval(secs => $1)";

    private readonly Lazy<string> _connectionString = new(() => ReadConnectionString(scope));

    public async Task<IDataStreamBatchWriter> BeginBatchAsync(CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);

        try
        {
            return new PostgresDataStreamBatchWriter(connection, await connection.BeginTransactionAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<StoredDataStreamHeader>> GetHeadersAsync(IReadOnlyCollection<Guid> streamIds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, HeadersSql, streamIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var headers = new List<StoredDataStreamHeader>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            headers.Add(new StoredDataStreamHeader(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));

        return headers;
    }

    public async Task<byte[]> ReadChunkAsync(Guid streamId, int chunkIndex, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, ChunkSql, streamId, chunkIndex);

        command.CommandTimeout = LongCommandSeconds;

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as byte[];
    }

    public async Task DeleteAsync(Guid streamId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, DeleteSql, streamId);

        command.CommandTimeout = LongCommandSeconds;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, DeleteOlderSql, age.TotalSeconds);

        command.CommandTimeout = LongCommandSeconds;

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString.Value);

        await connection.OpenAsync(ct).ConfigureAwait(false);

        return connection;
    }

    private static string ReadConnectionString(ILifetimeScope root)
    {
        using var child = root.BeginLifetimeScope();

        return child.Resolve<SquidDbContext>().Database.GetConnectionString();
    }

    internal static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);

        foreach (var value in values) command.Parameters.Add(Parameter(value));

        return command;
    }

    // A sealed chunk is passed as the writer's reused buffer, never copied into an array of its own.
    private static NpgsqlParameter Parameter(object value)
        => value is ReadOnlyMemory<byte> bytes ? new NpgsqlParameter<ReadOnlyMemory<byte>> { TypedValue = bytes, NpgsqlDbType = NpgsqlDbType.Bytea } : new NpgsqlParameter { Value = value };
}

/// <summary>One transaction on one connection: the streams written through it appear to readers all at once on commit, or never.</summary>
internal sealed class PostgresDataStreamBatchWriter(NpgsqlConnection connection, NpgsqlTransaction transaction) : IDataStreamBatchWriter
{
    private const string DeleteSql = "DELETE FROM halibut_data_stream WHERE stream_id = $1";
    private const string InsertHeaderSql = "INSERT INTO halibut_data_stream (stream_id, length, chunk_count, protected_key) VALUES ($1, 0, 0, $2)";
    private const string InsertChunkSql = "INSERT INTO halibut_data_stream_chunk (stream_id, chunk_index, content) VALUES ($1, $2, $3)";
    private const string FinishHeaderSql = "UPDATE halibut_data_stream SET length = $2, chunk_count = $3 WHERE stream_id = $1";

    public async Task BeginStreamAsync(Guid streamId, string protectedKey, CancellationToken ct)
    {
        await ExecuteAsync(DeleteSql, ct, streamId).ConfigureAwait(false);
        await ExecuteAsync(InsertHeaderSql, ct, streamId, protectedKey).ConfigureAwait(false);
    }

    public Task AddChunkAsync(Guid streamId, int chunkIndex, ReadOnlyMemory<byte> sealedChunk, CancellationToken ct) => ExecuteAsync(InsertChunkSql, ct, streamId, chunkIndex, sealedChunk);

    public Task CompleteStreamAsync(Guid streamId, long length, int chunkCount, CancellationToken ct) => ExecuteAsync(FinishHeaderSql, ct, streamId, length, chunkCount);

    public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

    private async Task ExecuteAsync(string sql, CancellationToken ct, params object[] values)
    {
        await using var command = PostgresDataStreamPersistence.Command(connection, sql, values);

        command.Transaction = transaction;
        command.CommandTimeout = PostgresDataStreamPersistence.LongCommandSeconds;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
