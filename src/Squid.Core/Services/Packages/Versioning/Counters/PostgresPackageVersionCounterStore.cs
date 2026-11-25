using Npgsql;
using Squid.Core.Halibut.DataStreamStorage;

namespace Squid.Core.Services.Packages.Versioning.Counters;

/// <summary>
/// <see cref="IPackageVersionCounterStore"/> on the <c>package_version_counter</c> table. One INSERT ... ON CONFLICT DO UPDATE ... RETURNING statement takes the
/// number under the row lock, on a connection of its own in autocommit, so it is committed at once and never rolled back with a caller's unit of work: a caller that
/// fails afterwards leaves a gap in the sequence and can never cause a number to be handed out twice.
/// </summary>
public sealed class PostgresPackageVersionCounterStore(ILifetimeScope lifetimeScope) : IPackageVersionCounterStore
{
    private const string NextSql = """
        INSERT INTO package_version_counter (feed_id, package_id, scope, value) VALUES ($1, $2, $3, 1)
        ON CONFLICT (feed_id, package_id, scope) DO UPDATE SET value = package_version_counter.value + 1, last_modified_date = now()
        RETURNING value
        """;

    private readonly Lazy<string> _connectionString = new(() => PostgresDataStreamPersistence.ReadConnectionString(lifetimeScope));

    public async Task<long> NextAsync(int feedId, string packageId, string scope, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString.Value);

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = PostgresDataStreamPersistence.Command(connection, NextSql, feedId, packageId, scope);

        return (long)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
