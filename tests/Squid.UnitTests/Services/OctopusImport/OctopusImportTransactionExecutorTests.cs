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
        await db.Database.EnsureCreatedAsync();

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
        await db.Database.EnsureCreatedAsync();

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
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    LastStateChangedAt = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);

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
        await db.Database.EnsureCreatedAsync();

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
}
