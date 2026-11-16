using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.Security;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.IntegrationTests.Services.Deployments.Variable;

/// <summary>
/// The compare-and-set statements behind filling a secret an import left empty, on the real database: the conditional UPDATE
/// writes only an empty sensitive value (or empty feed credentials) and nothing else — whatever the caller believed it read.
/// </summary>
public class SecretFillCompareAndSetIntegrationTests : TestBase
{
    public SecretFillCompareAndSetIntegrationTests() : base("SecretFillCompareAndSet", "squid_it_secret_fill_cas")
    {
    }

    [Theory]
    [InlineData(null, true, 1)]
    [InlineData("", true, 1)]
    [InlineData("SQUID_ENCRYPTED_V2:set-by-user", true, 0)]
    [InlineData(null, false, 0)]   // not sensitive: never written by a fill
    public async Task VariableCompareAndSet_WritesOnlyAnEmptySensitiveValue(string stored, bool sensitive, int expectedRows)
    {
        var (setId, variableId) = await SeedVariableAsync(stored, sensitive);

        var rows = await Run<IVariableDataProvider, int>(p => p.SetSensitiveValueIfEmptyAsync(variableId, "SQUID_ENCRYPTED_V2:filled"));

        rows.ShouldBe(expectedRows);
        (await StoredValueAsync(variableId)).ShouldBe(expectedRows == 1 ? "SQUID_ENCRYPTED_V2:filled" : stored);
        setId.ShouldBeGreaterThan(0);
    }

    // The service: located by identity (name, type, scope), encrypted with the set's key, version moved; a set value stays.
    [Theory]
    [InlineData("", "filled-value", SecretSlotState.Filled)]
    [InlineData("SQUID_ENCRYPTED_V2:set-by-user", "SQUID_ENCRYPTED_V2:set-by-user", SecretSlotState.AlreadySet)]
    public async Task FillEmptySensitiveValue_FindsByIdentity_WritesOnlyWhileEmpty(string stored, string expectedPlain, SecretSlotState expected)
    {
        var (setId, variableId) = await SeedVariableAsync(stored, true, environmentScope: "7");
        await SeedVariableAsync("", true, environmentScope: "8", variableSetId: setId);   // same name, another scope: another secret

        var identity = VariableIdentity.Of("Db.Password", VariableType.Password, [(VariableScopeType.Environment, "7")]);
        var state = await Run<IVariableService, SecretSlotState>(s => s.FillEmptySensitiveValueAsync(setId, identity, "filled-value", CancellationToken.None));

        state.ShouldBe(expected);
        var value = await StoredValueAsync(variableId);
        (expected == SecretSlotState.Filled ? await Run<IVariableEncryptionService, string>(e => e.DecryptAsync(value, setId)) : value).ShouldBe(expectedPlain);
        (await VersionAsync(setId)).ShouldBe(expected == SecretSlotState.Filled ? 2 : 1);
        (await Run<SquidDbContext, int>(db => db.Set<Core.Persistence.Entities.Deployments.Variable>().CountAsync(v => v.VariableSetId == setId && v.Value == ""))).ShouldBe(1, "the other scope's empty value is not touched");
    }

    // An import fills two secrets of one set in its own (Octopus) order inside one long transaction, and a Variables-page save of that
    // set starts between the two fills. Both writers lock the set's variables in id order before writing, so the save waits for the
    // import to commit; without that order the save would hold the first secret, the import the second, and Postgres would abort one.
    [Fact]
    public async Task SaveBetweenTwoFillsOfOneImport_WaitsForIt_NoDeadlock_BothFilledValuesKept()
    {
        var (setId, firstId) = await SeedVariableAsync("", true, environmentScope: "7");
        var (_, secondId) = await SeedVariableAsync("", true, environmentScope: "8", variableSetId: setId);
        Task save = null;

        await Run<SquidDbContext, IVariableService>(async (db, variables) =>
        {
            await using var import = await db.Database.BeginTransactionAsync();

            (await variables.FillEmptySensitiveValueAsync(setId, DbPasswordIn("8"), "second", CancellationToken.None)).ShouldBe(SecretSlotState.Filled);

            save = Task.Run(() => SaveMaskedAsync(setId));
            await WaitUntilAsync(async () => await WaitingBackendsAsync() >= 1, "the Variables save waits on the import's locks");

            (await variables.FillEmptySensitiveValueAsync(setId, DbPasswordIn("7"), "first", CancellationToken.None)).ShouldBe(SecretSlotState.Filled);

            await import.CommitAsync();
        });

        await save.WaitAsync(TimeSpan.FromSeconds(60));

        var values = await Run<SquidDbContext, List<string>>(db => db.Set<Core.Persistence.Entities.Deployments.Variable>().AsNoTracking().Where(v => v.VariableSetId == setId).OrderBy(v => v.Id).Select(v => v.Value).ToListAsync());

        values.Count.ShouldBe(2);
        values.ShouldAllBe(v => !string.IsNullOrEmpty(v), "the save posted both secrets masked: it keeps what the import filled");
        firstId.ShouldBeLessThan(secondId);
    }

    private static VariableIdentity DbPasswordIn(string environment) => VariableIdentity.Of("Db.Password", VariableType.Password, [(VariableScopeType.Environment, environment)]);

    private Task SaveMaskedAsync(int setId)
        => Run<IVariableService>(async variables =>
        {
            var saved = new[] { "7", "8" }.Select(env => new VariableModel { Name = "Db.Password", Value = MaskedSensitiveValues.Mask, Type = VariableType.Password, IsSensitive = true, Scopes = [new VariableScopeModel { ScopeType = VariableScopeType.Environment, ScopeValue = env }] }).ToList();

            await variables.UpdateVariableSetAsync(new UpdateVariableSetCommand { Id = setId, Name = "Project variables", OwnerType = VariableSetOwnerType.Project, SpaceId = 1, Variables = saved }, CancellationToken.None);
        });

    private Task<int> WaitingBackendsAsync()
        => Run<SquidDbContext, int>(db => db.Database.SqlQueryRaw<int>("""SELECT count(DISTINCT pid)::int AS "Value" FROM pg_locks WHERE NOT granted AND (database IS NULL OR database = (SELECT oid FROM pg_database WHERE datname = current_database()))""").SingleAsync());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting until {what} — check pg_locks / pg_stat_activity manually");

            await Task.Delay(100);
        }
    }

    [Theory]
    [InlineData(null, null, "robot", 1)]
    [InlineData("", null, "robot", 1)]
    [InlineData("robot", null, "robot", 1)]
    [InlineData("user-robot", null, "robot", 0)]
    [InlineData(null, "SQUID_ENCRYPTED_V2:user-token", "robot", 0)]
    [InlineData("robot", "SQUID_ENCRYPTED_V2:user-token", "robot", 0)]
    public async Task FeedCompareAndSet_WritesOnlyEmptyCredentials(string storedUsername, string storedPassword, string fillUsername, int expectedRows)
    {
        var feedId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var feed = new ExternalFeed { SpaceId = 1, Name = "cas-feed", Slug = $"feed-{Guid.NewGuid():N}", FeedType = "Docker", FeedUri = "https://registry.example.com", Username = storedUsername, Password = storedPassword };
            await repository.InsertAsync(feed);
            await unitOfWork.SaveChangesAsync();
            feedId = feed.Id;
        });

        var rows = await Run<IExternalFeedDataProvider, int>(p => p.SetCredentialsIfEmptyAsync(feedId, fillUsername, "registry-token"));
        var stored = await Run<SquidDbContext, ExternalFeed>(db => db.Set<ExternalFeed>().AsNoTracking().SingleAsync(f => f.Id == feedId));

        rows.ShouldBe(expectedRows);

        if (expectedRows == 0)
        {
            stored.Username.ShouldBe(storedUsername);
            stored.Password.ShouldBe(storedPassword);
            return;
        }

        stored.Username.ShouldBe("robot");
        stored.Password.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the filled password is protected at rest");
        (await Run<IAtRestSecretProtector, string>(p => p.UnprotectAsync(stored.Password, 0))).ShouldBe("registry-token");
    }

    private async Task<(int SetId, int VariableId)> SeedVariableAsync(string value, bool sensitive, string environmentScope = null, int variableSetId = 0)
    {
        var setId = variableSetId;
        var variableId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {

            if (setId == 0) setId = (await new TestDataBuilder(repository, unitOfWork).CreateVariableSetAsync()).Id;

            var variable = new Core.Persistence.Entities.Deployments.Variable { VariableSetId = setId, Name = "Db.Password", Value = value, Type = sensitive ? VariableType.Password : VariableType.String, IsSensitive = sensitive };
            await repository.InsertAsync(variable);
            await unitOfWork.SaveChangesAsync();

            if (environmentScope != null)
            {
                await repository.InsertAsync(new VariableScope { VariableId = variable.Id, ScopeType = VariableScopeType.Environment, ScopeValue = environmentScope });
                await unitOfWork.SaveChangesAsync();
            }

            variableId = variable.Id;
        });

        return (setId, variableId);
    }

    private Task<string> StoredValueAsync(int variableId)
        => Run<SquidDbContext, string>(db => db.Set<Core.Persistence.Entities.Deployments.Variable>().AsNoTracking().Where(v => v.Id == variableId).Select(v => v.Value).SingleAsync());

    private Task<int> VersionAsync(int setId)
        => Run<SquidDbContext, int>(db => db.Set<VariableSet>().AsNoTracking().Where(s => s.Id == setId).Select(s => s.Version).SingleAsync());
}
