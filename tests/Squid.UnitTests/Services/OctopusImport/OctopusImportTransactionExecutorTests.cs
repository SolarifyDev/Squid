using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Shouldly;
using Xunit;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportTransactionExecutorTests
{
    [Fact]
    public async Task ExecuteInImportTransactionAsync_CommitsWhenActionCompletes()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using var db = CreateDbContext(connection);
        await CreateImportSessionTableAsync(connection);

        var repository = new EfRepository(db);
        var sut = new OctopusImportTransactionExecutor(repository, db);
        var context = new OctopusImportTransactionContext(Guid.NewGuid(), 7);
        var sessionId = Guid.NewGuid();

        await sut.ExecuteInImportTransactionAsync(context, async (_, ct) =>
        {
            await repository.InsertAsync(new OctopusImportSession
            {
                SessionId = sessionId,
                DestinationSpaceId = 7,
                OwnerUserId = 42,
                State = "Uploaded",
                SourceSummaryJson = "{}",
                DataVersion = Guid.NewGuid().ToByteArray(),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                LastStateChangedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        });

        await using var verificationDb = CreateDbContext(connection);
        (await verificationDb.Set<OctopusImportSession>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteInImportTransactionAsync_RollsBackWhenActionFails()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using var db = CreateDbContext(connection);
        await CreateImportSessionTableAsync(connection);

        var repository = new EfRepository(db);
        var sut = new OctopusImportTransactionExecutor(repository, db);
        var context = new OctopusImportTransactionContext(Guid.NewGuid(), 7);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.ExecuteInImportTransactionAsync(context, async (_, ct) =>
            {
                await repository.InsertAsync(new OctopusImportSession
                {
                    SessionId = Guid.NewGuid(),
                    DestinationSpaceId = 7,
                    OwnerUserId = 42,
                    State = "Uploaded",
                    SourceSummaryJson = "{}",
                    DataVersion = Guid.NewGuid().ToByteArray(),
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    LastStateChangedAt = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);

                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                throw new InvalidOperationException("boom");
            }));

        await using var verificationDb = CreateDbContext(connection);
        (await verificationDb.Set<OctopusImportSession>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteInImportTransactionAsync_ReturnsTheActionResult()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        await using var db = CreateDbContext(connection);
        await CreateImportSessionTableAsync(connection);

        var repository = new EfRepository(db);
        var sut = new OctopusImportTransactionExecutor(repository, db);
        var context = new OctopusImportTransactionContext(Guid.NewGuid(), 7);

        var result = await sut.ExecuteInImportTransactionAsync(context, (_, _) => Task.FromResult("confirmed"));

        result.ShouldBe("confirmed");
    }

    private static SquidDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SquidDbContext(options);
    }

    private static async Task CreateImportSessionTableAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS octopus_import_session
            (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                destination_space_id INTEGER NOT NULL,
                owner_user_id INTEGER NOT NULL,
                state TEXT NOT NULL,
                source_summary_json TEXT NOT NULL DEFAULT '{}',
                redacted_normalized_data_json TEXT NULL,
                validated_plan_json TEXT NULL,
                result_json TEXT NULL,
                temporary_upload_path TEXT NULL,
                temporary_upload_size_bytes INTEGER NULL,
                temporary_upload_cleanup_after TEXT NULL,
                temporary_upload_cleaned_at TEXT NULL,
                temporary_upload_cleanup_error TEXT NULL,
                data_version BLOB NOT NULL,
                expires_at TEXT NOT NULL,
                completed_at TEXT NULL,
                last_state_changed_at TEXT NOT NULL,
                created_date TEXT NOT NULL,
                created_by INTEGER NOT NULL,
                last_modified_date TEXT NOT NULL,
                last_modified_by INTEGER NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync();
    }
}
