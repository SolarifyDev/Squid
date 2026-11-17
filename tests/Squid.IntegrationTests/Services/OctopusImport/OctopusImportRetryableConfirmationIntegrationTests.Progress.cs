using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Live confirmation progress against REAL Postgres: written on its own connection while the import transaction is open (so another
/// scope reads it mid-import), fenced on the attempt, never touching the data version, and never overwritten by the settle.
/// </summary>
public partial class OctopusImportRetryableConfirmationIntegrationTests
{
    private const long ProjectInsertGate = 918_273_645;

    // ── The write ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressWrite_LandsForTheHoldingAttempt_NoOpForAnother_DataVersionUnchanged()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        var before = await StoredAsync(sessionId);

        (await WriteProgressAsync(sessionId, attemptId, ProgressJson(attemptId, 3))).ShouldBe(1);
        (await WriteProgressAsync(sessionId, Guid.NewGuid(), ProgressJson(Guid.NewGuid(), 9))).ShouldBe(0, "another attempt's progress never lands");

        var after = await StoredAsync(sessionId);
        after.ConfirmationProgressJson.ShouldContain("\"itemsDone\": 3");
        after.DataVersion.ShouldBe(before.DataVersion, "the settle's compare-and-swap must still match");
        after.LastModifiedDate.ShouldBe(before.LastModifiedDate, "the resumable list is ordered by it");
    }

    // The final snapshot is written after the attempt's own settle: Succeeded keeps the attempt id, so it lands. Returning to Validated
    // releases the attempt, so a late write after it is a no-op (the orchestrator flushes the rolled-back snapshot before it).
    [Fact]
    public async Task ProgressWrite_LandsAfterTheSucceededSettle_NoOpAfterReturnToValidated()
    {
        var (succeededId, succeededAttempt) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        var (rolledBackId, rolledBackAttempt) = await SeedImportingAsync(DateTimeOffset.UtcNow);

        await Run<IOctopusImportSessionService>(s => s.SettleConfirmationAsync(succeededId, SpaceId, succeededAttempt, OctopusImportSessionState.Succeeded, new OctopusImportSessionResultDto()));
        await Run<IOctopusImportSessionService>(s => s.ReturnConfirmationToValidatedAsync(rolledBackId, SpaceId, new OctopusImportConfirmationFailureDto { AttemptId = rolledBackAttempt, Reason = OctopusImportConfirmationFailureReason.RolledBack }));

        (await WriteProgressAsync(succeededId, succeededAttempt, ProgressJson(succeededAttempt, 5))).ShouldBe(1);
        (await WriteProgressAsync(rolledBackId, rolledBackAttempt, ProgressJson(rolledBackAttempt, 5))).ShouldBe(0);
        (await SessionAsync(succeededId)).ConfirmationProgress.ShouldNotBeNull().ItemsDone.ShouldBe(5);
    }

    [Fact]
    public async Task Admission_StartsWithNoProgress()
    {
        var (sessionId, _) = await SeedImportingAsync(null, state: OctopusImportSessionState.Validated);
        await Run<SquidDbContext>(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE octopus_import_session SET confirmation_progress_json = {ProgressJson(Guid.NewGuid(), 7)}::jsonb WHERE session_id = {sessionId}"));

        (await Run<IOctopusImportSessionService, bool>(s => s.TryStartConfirmationAsync(sessionId, SpaceId, Guid.NewGuid(), null))).ShouldBeTrue();

        (await StoredAsync(sessionId)).ConfirmationProgressJson.ShouldBeNull("a new attempt never shows the previous attempt's counts");
    }

    // The settle writes the session it read before; progress written after that read must survive it.
    [Fact]
    public async Task Settle_NeverOverwritesProgressWrittenAfterItsRead()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        var read = await StoredAsync(sessionId);

        await WriteProgressAsync(sessionId, attemptId, ProgressJson(attemptId, 4));
        await Run<IOctopusImportSessionDataProvider>(p => p.UpdateSessionAsync(read, read.DataVersion));

        (await StoredAsync(sessionId)).ConfirmationProgressJson.ShouldContain("\"itemsDone\": 4");
    }

    // A progress write that reaches the session row while the import transaction holds it (its settle) waits for the commit, then lands:
    // the import never waits for it, and nothing deadlocks.
    [Fact]
    public async Task ProgressWrite_BlockedByTheSettlesRowLock_LandsAfterTheCommit()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        Task<int> write = null;

        await Run<IOctopusImportTransactionExecutor, IOctopusImportSessionService>(async (executor, sessions) =>
        {
            await executor.ExecuteInImportTransactionAsync(new OctopusImportTransactionContext(sessionId, SpaceId), async (_, ct) =>
            {
                var settled = await sessions.SettleConfirmationAsync(sessionId, SpaceId, attemptId, OctopusImportSessionState.Succeeded, new OctopusImportSessionResultDto(), ct);

                write = WriteProgressAsync(sessionId, attemptId, ProgressJson(attemptId, 6));
                (await Task.WhenAny(write, Task.Delay(500, ct))).ShouldNotBe(write, "the write must wait behind the uncommitted settle's row lock");

                return settled;
            });
        });

        (await write.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(1, "after the commit the attempt id still matches, so the write lands");
        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
        stored.ConfirmationProgressJson.ShouldContain("\"itemsDone\": 6");
    }

    // ── Through the real confirmation ──────────────────────────────────────────────────────

    // The point of the feature: a reader on another scope sees the import advance while its single transaction is still open.
    [Fact]
    public async Task RealConfirmation_ProgressVisibleMidImportFromAnotherScope_ThenFinalSucceeded()
    {
        await WithArchiveAsync(async (archivePath, _) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            await InstallProjectInsertGateAsync();

            await Run<SquidDbContext>(async gate =>
            {
                await gate.Database.OpenConnectionAsync();
                await gate.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_lock({ProjectInsertGate})");

                var confirm = ConfirmAsync(sessionId);
                var midway = await WaitForProgressAsync(sessionId, p => p.Current?.Kind == "Project");

                midway.Stage.ShouldBe(OctopusImportConfirmationStage.Importing);
                midway.ItemsDone.ShouldBeGreaterThan(0, "the items before the project were counted");
                midway.ItemsDone.ShouldBeLessThan(midway.ItemsTotal);
                midway.Current.Name.ShouldNotBeNullOrWhiteSpace();
                (await SessionAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Importing);

                await gate.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({ProjectInsertGate})");
                (await confirm).Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
            });

            var final = (await SessionAsync(sessionId)).ConfirmationProgress.ShouldNotBeNull();
            final.ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.Finished.ShouldBeTrue(), p => p.ItemsDone.ShouldBe(p.ItemsTotal), p => p.RecentLog.Count.ShouldBeLessThanOrEqualTo(20));
        });
    }

    // Rolled back: the session is Validated again showing the attempt that rolled back; importing again shows a fresh run.
    [Fact]
    public async Task RealConfirmation_Rollback_ShowsRolledBackProgress_ThenImportAgainStartsFresh()
    {
        await WithArchiveAsync(async (archivePath, _) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            await InjectFaultAsync("RAISE EXCEPTION 'injected bug' USING ERRCODE = 'P0001'", times: int.MaxValue);

            await ConfirmAsync(sessionId);

            var rolledBack = await SessionAsync(sessionId);
            rolledBack.State.ShouldBe(OctopusImportSessionState.Validated);
            var progress = rolledBack.ConfirmationProgress.ShouldNotBeNull("the rolled-back snapshot was written before the attempt was released");
            progress.ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.RolledBack), p => p.Finished.ShouldBeTrue(), p => p.AttemptId.ShouldBe(rolledBack.LastConfirmationFailure.AttemptId));

            await DropFaultAsync();
            (await ConfirmAsync(sessionId)).Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);

            var again = (await SessionAsync(sessionId)).ConfirmationProgress.ShouldNotBeNull();
            again.ShouldSatisfyAllConditions(p => p.AttemptId.ShouldNotBe(progress.AttemptId), p => p.Attempt.ShouldBe(1), p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.ItemsDone.ShouldBe(p.ItemsTotal));
            again.RecentLog.ShouldNotContain(e => e.Stage == OctopusImportConfirmationStage.RolledBack);
        });
    }

    [Fact]
    public async Task Container_RegistersTheProgressWriter()
    {
        (await Run<IOctopusImportConfirmationProgress, Type>(progress => Task.FromResult(progress.GetType()))).ShouldBe(typeof(OctopusImportConfirmationProgress));
    }

    [Fact]
    public async Task ProgressMigration_RunTwice_Idempotent()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        await WriteProgressAsync(sessionId, attemptId, ProgressJson(attemptId, 2));
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, "20261119_add_octopus_import_session_confirmation_progress.sql"));

        await Run<SquidDbContext>(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(script);
            await db.Database.ExecuteSqlRawAsync(script);
        });

        (await StoredAsync(sessionId)).ConfirmationProgressJson.ShouldContain("\"itemsDone\": 2");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private Task<int> WriteProgressAsync(Guid sessionId, Guid attemptId, string json) => Run<IOctopusImportSessionDataProvider, int>(p => p.WriteConfirmationProgressAsync(sessionId, attemptId, json));

    private static string ProgressJson(Guid attemptId, int itemsDone) => OctopusImportSessionService.SerializeProgress(new OctopusImportConfirmationProgressDto { AttemptId = attemptId, Stage = OctopusImportConfirmationStage.Importing, ItemsDone = itemsDone, ItemsTotal = 10 });

    private async Task<OctopusImportConfirmationProgressDto> WaitForProgressAsync(Guid sessionId, Func<OctopusImportConfirmationProgressDto, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var progress = (await SessionAsync(sessionId)).ConfirmationProgress;

            if (progress != null && condition(progress)) return progress;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Session {sessionId} showed no matching confirmation progress within 30s: the progress writer did not land a snapshot outside the import transaction. Check confirmation_progress_json on the row manually.");
    }

    // Every project insert waits while the test holds the advisory lock: the import pauses inside its open transaction.
    private Task InstallProjectInsertGateAsync() => Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync(
        "CREATE OR REPLACE FUNCTION it_project_insert_gate() RETURNS trigger LANGUAGE plpgsql AS $fn$ BEGIN PERFORM pg_advisory_lock_shared(" + ProjectInsertGate + "); PERFORM pg_advisory_unlock_shared(" + ProjectInsertGate + "); RETURN NEW; END $fn$; " +
        "DROP TRIGGER IF EXISTS it_project_insert_gate ON project; CREATE TRIGGER it_project_insert_gate BEFORE INSERT ON project FOR EACH ROW EXECUTE FUNCTION it_project_insert_gate();"));

    private Task DropProjectInsertGateAsync() => Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS it_project_insert_gate ON project; DROP FUNCTION IF EXISTS it_project_insert_gate();"));
}
