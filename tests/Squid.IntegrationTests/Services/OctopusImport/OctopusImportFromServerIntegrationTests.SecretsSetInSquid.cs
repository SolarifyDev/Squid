using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Secrets an API import left missing, saved through the real Variables page service: a value the user set and then cleared is the
/// user's edit (no export fills it again), and a save racing an import's uncommitted fill never re-inserts the secret empty under a
/// row that says it was filled.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    // Set then cleared in Squid, with no import in between to see the value: the clear is still the user's edit.
    [Fact]
    public async Task SecretSetThenClearedInSquid_NoImportBetween_FileNeverRefills()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        await SaveDbPasswordThroughVariablesPageAsync("k1");
        (await DbPasswordAsync()).ShouldBe("k1");

        await SaveDbPasswordThroughVariablesPageAsync("");
        (await VariableAsync("Db.Password")).Value.ShouldBe("", "the user's clear is stored as empty");

        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true));

        (await DbPasswordAsync()).ShouldBeNullOrEmpty("a secret the user cleared in Squid is an edit made in Squid: no import may refill it");
    }

    // A Variables-page save that posts the mask while the file import's fill is uncommitted waits for it and keeps the filled value.
    [Fact]
    public async Task VariablesSaveDuringConfirmation_WaitsForTheFill_KeepsTheFilledValue()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var archive = SameInstallationExport(FileFingerprint);
        Task<OctopusImportFlowResult> import = null;
        Task uiSave = null;

        await Run<SquidDbContext>(async lockDb =>
        {
            await using var gate = await lockDb.Database.BeginTransactionAsync();
            await lockDb.Database.ExecuteSqlRawAsync("LOCK TABLE octopus_import_source_map IN EXCLUSIVE MODE");

            import = Task.Run(() => ImportFromFileAsync(archive, confirmPairing: true));
            await WaitUntilAsync(() => ImportFilledAndWaitsOnSourceMapAsync(), "the import filled the variable and then waited on the source-map lock");

            uiSave = Task.Run(() => SaveDbPasswordThroughVariablesPageAsync(MaskedSensitiveValues.Mask));
            await WaitUntilAsync(async () => await WaitingBackendsAsync() >= 2, "the Variables save waits on the import's row lock");

            await gate.CommitAsync();
        });

        ShouldHaveSucceeded(await import);
        await uiSave;

        var projectSetId = (await VariableAsync("Db.Password")).VariableSetId;
        var row = (await SecretRowsAsync()).Single(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType && r.DestinationId == projectSetId);

        row.OutcomeState.ShouldBe(OctopusImportSourceMapQueries.FilledOutcome);
        (await DbPasswordAsync()).ShouldNotBeNullOrEmpty("the save re-inserted the secret empty under a row that says it was filled: no later import ever fills it again");
    }

    // The project's set saved through the real service, Db.Password posted with the given value (the mask = untouched in the page).
    private Task SaveDbPasswordThroughVariablesPageAsync(string value)
        => Run<SquidDbContext, IVariableService>(async (db, variables) =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync();
            var set = await db.Set<VariableSet>().AsNoTracking().SingleAsync(s => s.Id == project.VariableSetId);
            var dbPassword = await db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Db.Password" && v.VariableSetId == set.Id);
            var scopes = await db.Set<VariableScope>().AsNoTracking().Where(s => s.VariableId == dbPassword.Id).Select(s => new VariableScopeModel { ScopeType = s.ScopeType, ScopeValue = s.ScopeValue }).ToListAsync();
            var saved = new List<VariableModel> { new() { Name = "Db.Password", Value = value, Type = dbPassword.Type, IsSensitive = true, Scopes = scopes } };

            await variables.UpdateVariableSetAsync(new UpdateVariableSetCommand { Id = set.Id, Name = set.Name, Description = set.Description, OwnerId = project.Id, OwnerType = VariableSetOwnerType.Project, SpaceId = SpaceId, Variables = saved }, CancellationToken.None);
            await db.SaveChangesAsync();
        });

    private Task<bool> ImportFilledAndWaitsOnSourceMapAsync()
        => Run<SquidDbContext, bool>(db => db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
              SELECT 1 FROM pg_locks w JOIN pg_locks v ON v.pid = w.pid
              WHERE NOT w.granted AND w.relation = 'octopus_import_source_map'::regclass
                AND v.granted AND v.relation = 'variable'::regclass AND v.mode = 'RowExclusiveLock') AS "Value"
            """).SingleAsync());

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
}
