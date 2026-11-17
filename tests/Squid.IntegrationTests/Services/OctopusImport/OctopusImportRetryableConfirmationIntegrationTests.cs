using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier (REAL Postgres, REAL container, REAL mediator pipeline) for the retryable confirmation: faults are real
/// <c>PostgresException</c>s raised by a trigger on a destination table inside the real import transaction, so the rollback, the
/// automatic retry, the return to Validated, the heartbeat on its own connection and the recovery sweep all run against real rows.
/// </summary>
public partial class OctopusImportRetryableConfirmationIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string ExportPassword = "fake-export-password-for-tests";

    public OctopusImportRetryableConfirmationIntegrationTests() : base("OctopusImportRetryableConfirmation", "squid_it_octopus_import_retryable_confirmation")
    {
    }

    // ── Confirmation through the real pipeline ─────────────────────────────────────────────────

    [Fact]
    public async Task SerializationFailureOnFirstAttempt_RetriesAndCommitsExactlyOnce()
    {
        await WithArchiveAsync(async (archivePath, projectName) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            await InjectFaultAsync("RAISE EXCEPTION 'injected serialization failure' USING ERRCODE = '40001'", times: 1);

            var confirm = await ConfirmAsync(sessionId);

            confirm.Code.ShouldBe(HttpStatusCode.OK, customMessage: confirm.Msg);
            confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
            (await FaultsRaisedAsync()).ShouldBe(2, "the first attempt hit the fault, the retry passed it");
            (await CountAsync<Project>(p => p.Name == projectName)).ShouldBe(1);
            (await CountAsync<ProjectGroup>(_ => true)).ShouldBe(1);
        });
    }

    // The connection itself dies mid-transaction: the retry runs on a fresh connection in the same scope.
    [Fact]
    public async Task ConnectionTerminatedMidTransaction_RetriesOnFreshConnection()
    {
        await WithArchiveAsync(async (archivePath, projectName) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            await InjectFaultAsync("PERFORM pg_terminate_backend(pg_backend_pid())", times: 1);

            var confirm = await ConfirmAsync(sessionId);

            confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: confirm.Msg);
            (await FaultsRaisedAsync()).ShouldBe(2, "the first attempt lost its connection, the retry passed the fault on a new one");
            (await CountAsync<Project>(p => p.Name == projectName)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task PersistentNonTransientFailure_RollsBackToValidated_NothingWritten()
    {
        await WithArchiveAsync(async (archivePath, projectName) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            var before = await StoredAsync(sessionId);
            await InjectFaultAsync("RAISE EXCEPTION 'injected bug' USING ERRCODE = 'P0001'", times: int.MaxValue);

            var confirm = await ConfirmAsync(sessionId);

            confirm.Code.ShouldBe(HttpStatusCode.BadRequest);
            confirm.Data.BlockerSummary.BlockingReasons.ShouldNotBeEmpty();
            var session = await SessionAsync(sessionId);
            session.State.ShouldBe(OctopusImportSessionState.Validated);
            session.LastConfirmationFailure.ShouldNotBeNull().Reason.ShouldBe(OctopusImportConfirmationFailureReason.RolledBack);
            session.LastConfirmationFailure.Attempts.ShouldBe(1);
            (await FaultsRaisedAsync()).ShouldBe(1, "a non-transient failure is not retried");
            (await CountAsync<Project>(p => p.Name == projectName)).ShouldBe(0);
            (await CountAsync<OctopusImportSourceMap>(_ => true)).ShouldBe(0);
            var stored = await StoredAsync(sessionId);
            stored.ProtectedExportPassword.ShouldNotBeNull("the session is not over: the next attempt needs the password");
            stored.DataVersion.ShouldNotBe(before.DataVersion);
            stored.ValidatedPlanJson.ShouldNotBeNull("the same plan is imported again, without reading the source again");
            stored.ConfirmationAttemptId.ShouldBeNull();
        });
    }

    // The never-overwrite proof: after a rolled-back attempt, importing the same validated plan again (no re-read, no re-validate)
    // writes exactly what a clean single import of the same shape writes.
    [Fact]
    public async Task ImportAgainAfterRollback_WithoutReread_MatchesCleanRun()
    {
        var (cleanProjects, cleanGroups, cleanSourceMap) = await CleanRunCountsAsync();

        await WithArchiveAsync(async (archivePath, projectName) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            var (projectsBefore, groupsBefore, sourceMapBefore) = await CountsAsync();
            await InjectFaultAsync("RAISE EXCEPTION 'injected bug' USING ERRCODE = 'P0001'", times: int.MaxValue);

            (await ConfirmAsync(sessionId)).Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
            await DropFaultAsync();

            var again = await ConfirmAsync(sessionId);

            again.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: again.Msg);
            again.Data.Session.LastConfirmationFailure.ShouldBeNull();
            var (projectsAfter, groupsAfter, sourceMapAfter) = await CountsAsync();
            (projectsAfter - projectsBefore, groupsAfter - groupsBefore, sourceMapAfter - sourceMapBefore).ShouldBe((cleanProjects, cleanGroups, cleanSourceMap));
            (await CountAsync<Project>(p => p.Name == projectName)).ShouldBe(1);
        });
    }

    // A rolled-back attempt's failure describes the plan it tried: once the plan is validated again or the selection changes, the
    // status reports the new plan's state, never "rolled back, import again" for a plan nobody tried.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlanReplacedAfterRollback_StatusNoLongerReportsTheOldFailure(bool revalidate)
    {
        await WithArchiveAsync(async (archivePath, _) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            await InjectFaultAsync("RAISE EXCEPTION 'injected bug' USING ERRCODE = 'P0001'", times: int.MaxValue);
            (await ConfirmAsync(sessionId)).Data.Session.LastConfirmationFailure.ShouldNotBeNull();

            if (revalidate) await RevalidateAsync(sessionId);
            else await Run<IOctopusImportSessionService>(async s => await s.UpdateProjectSelectionAsync(sessionId, SpaceId, null, (await StoredAsync(sessionId)).DataVersion));

            var status = await StatusAsync(sessionId);
            status.Session.State.ShouldBe(OctopusImportSessionState.Validated);
            status.Session.LastConfirmationFailure.ShouldBeNull("the failure belonged to the replaced plan");
            status.BlockerSummary.BlockingReasons.ShouldBeEmpty("no blocker may come from the replaced plan's rolled-back attempt");
            (await StoredAsync(sessionId)).LastConfirmationFailureJson.ShouldBeNull();
        });
    }

    // A new attempt holds the session: the previous attempt's failure is history, not the state of the session.
    [Fact]
    public async Task Admission_ForgetsThePreviousAttemptsFailure()
    {
        var (sessionId, _) = await SeedImportingAsync(null, state: OctopusImportSessionState.Validated, failureJson: OctopusImportSessionService.SerializeFailure(new OctopusImportConfirmationFailureDto { AttemptId = Guid.NewGuid(), Reason = OctopusImportConfirmationFailureReason.RolledBack }));

        (await Run<IOctopusImportSessionService, bool>(s => s.TryStartConfirmationAsync(sessionId, SpaceId, Guid.NewGuid(), null))).ShouldBeTrue();

        (await StoredAsync(sessionId)).LastConfirmationFailureJson.ShouldBeNull();
    }

    // ── Heartbeat ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_TouchesOnlyTheHeartbeat_DataVersionUnchanged()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var before = await StoredAsync(sessionId);
        var databaseBefore = await DatabaseNowAsync();

        (await Run<IOctopusImportSessionDataProvider, int>(p => p.TouchConfirmationHeartbeatAsync(sessionId, attemptId))).ShouldBe(1);
        (await Run<IOctopusImportSessionDataProvider, int>(p => p.TouchConfirmationHeartbeatAsync(sessionId, Guid.NewGuid()))).ShouldBe(0, "another attempt's beat never touches the session");

        var after = await StoredAsync(sessionId);
        after.ImportHeartbeatAt.ShouldNotBeNull().ShouldBeInRange(databaseBefore, await DatabaseNowAsync(), "stamped by the database clock, the one the recovery sweep measures it against");
        after.DataVersion.ShouldBe(before.DataVersion);
        after.LastModifiedDate.ShouldBe(before.LastModifiedDate);
        after.State.ShouldBe(OctopusImportSessionState.Importing.ToString());
    }

    // The beat lands at once on its own connection while the import transaction is still open, and the import's final settle, a
    // compare-and-swap on the data version, still commits after it.
    [Fact]
    public async Task Heartbeat_VisibleWhileImportTransactionOpen_AndFinalSettleStillCommits()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var databaseBefore = await DatabaseNowAsync();

        await Run<IOctopusImportTransactionExecutor, IOctopusImportSessionService>(async (executor, sessions) =>
        {
            await executor.ExecuteInImportTransactionAsync(new OctopusImportTransactionContext(sessionId, SpaceId), async (_, ct) =>
            {
                await sessions.GetSessionAsync(sessionId, SpaceId, ct);

                (await Run<IOctopusImportSessionDataProvider, int>(p => p.TouchConfirmationHeartbeatAsync(sessionId, attemptId))).ShouldBe(1);
                (await StoredAsync(sessionId)).ImportHeartbeatAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(databaseBefore);

                return await sessions.SettleConfirmationAsync(sessionId, SpaceId, attemptId, OctopusImportSessionState.Succeeded, new OctopusImportSessionResultDto(), ct);
            });
        });

        (await StoredAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
    }

    // The orchestrator takes the heartbeat as an optional dependency: only the real container proves it is registered and injected, and
    // without it every import longer than the stale threshold would be recovered mid-flight and rolled back at its settle.
    [Fact]
    public async Task Container_RegistersTheHeartbeat_AndARealConfirmationBeatsForItsAttempt()
    {
        (await Run<IOctopusImportConfirmationHeartbeat, Type>(heartbeat => Task.FromResult(heartbeat.GetType()))).ShouldBe(typeof(OctopusImportConfirmationHeartbeat));

        await WithArchiveAsync(async (archivePath, _) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            var recording = new RecordingHeartbeat();

            var confirm = await Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId), builder =>
            {
                builder.RegisterInstance(AllowAllAuthorization()).As<IAuthorizationService>();
                builder.RegisterInstance(recording).As<IOctopusImportConfirmationHeartbeat>();
            });

            confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: confirm.Msg);
            recording.Started.ShouldHaveSingleItem().SessionId.ShouldBe(sessionId);
            recording.Disposed.ShouldBe(1, "the beat stops when the confirmation ends");
        });
    }

    [Fact]
    public async Task SettleWithForeignAttempt_ThrowsAndRollsBack()
    {
        var (sessionId, _) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        var groupName = $"Group-{Guid.NewGuid():N}";

        await Run<IOctopusImportTransactionExecutor, IOctopusImportSessionService, SquidDbContext>(async (executor, sessions, db) =>
        {
            await Should.ThrowAsync<OctopusImportConfirmationFencedException>(() => executor.ExecuteInImportTransactionAsync(new OctopusImportTransactionContext(sessionId, SpaceId), async (_, ct) =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO project_group (name, slug, space_id, description, created_date, created_by, last_modified_date, last_modified_by) VALUES ({groupName}, {groupName}, {SpaceId}, '', now(), 0, now(), 0)", ct);

                return await sessions.SettleConfirmationAsync(sessionId, SpaceId, Guid.NewGuid(), OctopusImportSessionState.Succeeded, new OctopusImportSessionResultDto(), ct);
            }));
        });

        (await StoredAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Importing.ToString());
        (await CountAsync<ProjectGroup>(g => g.Name == groupName)).ShouldBe(0, "the fenced attempt's writes rolled back with it");
    }

    [Fact]
    public async Task ImportTransaction_SetsIdleInTransactionTimeout()
    {
        var setting = await Run<ILifetimeScope, string>(scope => scope.Resolve<IOctopusImportTransactionExecutor>().ExecuteInImportTransactionAsync(new OctopusImportTransactionContext(Guid.NewGuid(), SpaceId) { IdleInTransactionTimeout = TimeSpan.FromMinutes(5) }, (_, ct) => scope.Resolve<IRepository>().Database.SqlQueryRaw<string>("SELECT current_setting('idle_in_transaction_session_timeout') AS \"Value\"").SingleAsync(ct)));

        setting.ShouldBe("5min");
        (await Run<IRepository, string>(repository => repository.Database.SqlQueryRaw<string>("SELECT current_setting('idle_in_transaction_session_timeout') AS \"Value\"").SingleAsync())).ShouldBe("0", "SET LOCAL ends with the transaction");
    }

    // ── Recovery ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recover_StaleHeartbeat_ValidatedWithInterruptedFailure_PasswordKept()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow.AddMinutes(-10));

        (await Run<IOctopusImportConfirmationRecoveryService, int>(s => s.RecoverInterruptedAsync())).ShouldBe(1);

        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Validated.ToString());
        stored.ProtectedExportPassword.ShouldNotBeNull();
        var session = await SessionAsync(sessionId);
        session.LastConfirmationFailure.ShouldNotBeNull().ShouldSatisfyAllConditions(f => f.Reason.ShouldBe(OctopusImportConfirmationFailureReason.Interrupted), f => f.AttemptId.ShouldBe(attemptId));
    }

    [Theory]
    [InlineData(-1, null, false)]        // a fresh heartbeat: the import is alive
    [InlineData(null, -7 * 60, true)]    // no heartbeat (an older server's import), older than the 6h grace: recovered
    [InlineData(null, -60, false)]       // no heartbeat, younger than the grace: trusted
    public async Task Recover_HeartbeatAndLegacyCutoffs(int? heartbeatMinutesAgo, int? stateChangedMinutesAgo, bool expectRecovered)
    {
        var now = DateTimeOffset.UtcNow;
        var (sessionId, _) = await SeedImportingAsync(heartbeatMinutesAgo is { } beat ? now.AddMinutes(beat) : null, stateChangedMinutesAgo is { } changed ? now.AddMinutes(changed) : now, legacy: heartbeatMinutesAgo == null);

        await Run<IOctopusImportConfirmationRecoveryService, int>(s => s.RecoverInterruptedAsync());

        (await StoredAsync(sessionId)).State.ShouldBe(expectRecovered ? OctopusImportSessionState.Validated.ToString() : OctopusImportSessionState.Importing.ToString());
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Failed)]
    [InlineData(OctopusImportSessionState.Validated)]
    public async Task Recover_NotImporting_Untouched(OctopusImportSessionState state)
    {
        var (sessionId, _) = await SeedImportingAsync(DateTimeOffset.UtcNow.AddHours(-1), state: state);
        var before = await StoredAsync(sessionId);

        await Run<IOctopusImportConfirmationRecoveryService, int>(s => s.RecoverInterruptedAsync());

        var after = await StoredAsync(sessionId);
        after.State.ShouldBe(state.ToString());
        after.DataVersion.ShouldBe(before.DataVersion);
    }

    // Two confirmations racing after a recovery: the admission is atomic, so only one holds the session.
    [Fact]
    public async Task TwoConcurrentConfirmsAfterRecovery_OnlyOneAdmitted()
    {
        var (sessionId, _) = await SeedImportingAsync(DateTimeOffset.UtcNow.AddMinutes(-10));
        await Run<IOctopusImportConfirmationRecoveryService, int>(s => s.RecoverInterruptedAsync());

        var results = await Task.WhenAll(Run<IOctopusImportSessionService, bool>(s => s.TryStartConfirmationAsync(sessionId, SpaceId, Guid.NewGuid(), null)), Run<IOctopusImportSessionService, bool>(s => s.TryStartConfirmationAsync(sessionId, SpaceId, Guid.NewGuid(), null)));

        results.Count(admitted => admitted).ShouldBe(1);
        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Importing.ToString());
        stored.ConfirmationAttemptId.ShouldNotBeNull();
        stored.ImportHeartbeatAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Migration_RunTwiceOnExistingRows_Idempotent()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, "20261118_add_octopus_import_session_confirmation_heartbeat.sql"));

        await Run<SquidDbContext>(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(script);
            await db.Database.ExecuteSqlRawAsync(script);
        });

        (await StoredAsync(sessionId)).ConfirmationAttemptId.ShouldBe(attemptId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private async Task WithArchiveAsync(Func<string, string, Task> test)
    {
        var projectName = $"Retryable-{Guid.NewGuid():N}";
        var archivePath = OctopusExportSecretsArchive.Write(projectName, sensitiveValue: "s3cret-value", exportPassword: ExportPassword);

        try
        {
            await test(archivePath, projectName);
        }
        finally
        {
            File.Delete(archivePath);
            await DropFaultAsync();
            await DropProjectInsertGateAsync();
        }
    }

    private async Task<(int Projects, int Groups, int SourceMap)> CleanRunCountsAsync()
    {
        var before = await CountsAsync();

        await WithArchiveAsync(async (archivePath, _) => (await ConfirmAsync(await ValidateAsync(archivePath))).Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded));

        var after = await CountsAsync();

        return (after.Projects - before.Projects, after.Groups - before.Groups, after.SourceMap - before.SourceMap);
    }

    private async Task<(int Projects, int Groups, int SourceMap)> CountsAsync() => (await CountAsync<Project>(_ => true), await CountAsync<ProjectGroup>(_ => true), await CountAsync<OctopusImportSourceMap>(_ => true));

    private async Task<Guid> ValidateAsync(string archivePath)
    {
        var sessionId = Guid.Empty;

        await Run<IMediator>(async mediator =>
        {
            var upload = await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archivePath, ExportPassword);
            upload.Code.ShouldBe(HttpStatusCode.OK, customMessage: upload.Msg);
            sessionId = upload.Data.Session.SessionId;
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

            var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
            preview.Data.PreviewPlan.HasBlockers.ShouldBeFalse(OctopusImportHandlerFlow.DescribeBlockers(OctopusImportHandlerFlow.AllDiagnostics(preview.Data.PreviewPlan)));

            var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
            validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: validate.Msg);
        });

        return sessionId;
    }

    // The integration owner has no team roles: permissions are another test's concern, here every check passes.
    private Task<Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse> ConfirmAsync(Guid sessionId) => Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId), builder => builder.RegisterInstance(AllowAllAuthorization()).As<IAuthorizationService>());

    private static IAuthorizationService AllowAllAuthorization()
    {
        var authorization = new Moq.Mock<IAuthorizationService>();
        authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return authorization.Object;
    }

    // A real Postgres error from inside the real import transaction: the first `times` project-group inserts raise it. The counter is
    // a sequence, which a rollback does not undo, so "once" stays once across the retried transaction.
    private Task InjectFaultAsync(string fault, int times) => Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync(
        "DROP TRIGGER IF EXISTS it_injected_fault ON project_group; DROP SEQUENCE IF EXISTS it_injected_fault_seq; CREATE SEQUENCE it_injected_fault_seq; " +
        "CREATE OR REPLACE FUNCTION it_injected_fault() RETURNS trigger LANGUAGE plpgsql AS $fn$ BEGIN IF nextval('it_injected_fault_seq') <= " + times + " THEN " + fault + "; END IF; RETURN NEW; END $fn$; " +
        "CREATE TRIGGER it_injected_fault BEFORE INSERT ON project_group FOR EACH ROW EXECUTE FUNCTION it_injected_fault();"));

    private Task DropFaultAsync() => Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS it_injected_fault ON project_group; DROP FUNCTION IF EXISTS it_injected_fault(); DROP SEQUENCE IF EXISTS it_injected_fault_seq;"));

    private Task<long> FaultsRaisedAsync() => Run<SquidDbContext, long>(db => db.Database.SqlQueryRaw<long>("SELECT last_value AS \"Value\" FROM it_injected_fault_seq").SingleAsync());

    private async Task<(Guid SessionId, Guid AttemptId)> SeedImportingAsync(DateTimeOffset? heartbeat, DateTimeOffset? stateChangedAt = null, bool legacy = false, OctopusImportSessionState state = OctopusImportSessionState.Importing, string failureJson = null)
    {
        var (sessionId, attemptId) = (Guid.NewGuid(), Guid.NewGuid());
        var session = new OctopusImportSession { SessionId = sessionId, DestinationSpaceId = SpaceId, OwnerUserId = CurrentUsers.InternalUser.Id, State = state.ToString(), SourceSummaryJson = "{}", DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastStateChangedAt = stateChangedAt ?? DateTimeOffset.UtcNow, ProtectedExportPassword = OctopusImportSessionStateMachine.IsTerminal(state) ? null : "SQUID_ENCRYPTED_V2:retained", ConfirmationAttemptId = legacy ? null : attemptId, ImportHeartbeatAt = heartbeat, LastConfirmationFailureJson = failureJson };

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(session);
            await db.SaveChangesAsync();
        });

        return (sessionId, attemptId);
    }

    private Task RevalidateAsync(Guid sessionId) => Run<IMediator>(async mediator =>
    {
        var preview = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
        var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview.Data.PreviewPlan);
        validate.Code.ShouldBe(HttpStatusCode.OK, customMessage: validate.Msg);
    });

    private async Task<Squid.Message.Requests.OctopusImport.GetOctopusImportStatusResponseData> StatusAsync(Guid sessionId)
        => (await Run<IMediator, Squid.Message.Requests.OctopusImport.GetOctopusImportStatusResponse>(mediator => mediator.RequestAsync<Squid.Message.Requests.OctopusImport.GetOctopusImportStatusRequest, Squid.Message.Requests.OctopusImport.GetOctopusImportStatusResponse>(new Squid.Message.Requests.OctopusImport.GetOctopusImportStatusRequest { SpaceId = SpaceId, SessionId = sessionId }), builder => builder.RegisterInstance(AllowAllAuthorization()).As<IAuthorizationService>())).Data;

    private Task<DateTimeOffset> DatabaseNowAsync() => Run<SquidDbContext, DateTimeOffset>(db => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync());

    private sealed class RecordingHeartbeat : IOctopusImportConfirmationHeartbeat
    {
        public List<(Guid SessionId, Guid AttemptId)> Started { get; } = [];
        public int Disposed { get; private set; }

        public IAsyncDisposable Start(Guid sessionId, Guid attemptId)
        {
            Started.Add((sessionId, attemptId));

            return new Handle(this);
        }

        private sealed class Handle(RecordingHeartbeat owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Disposed++;

                return ValueTask.CompletedTask;
            }
        }
    }

    private Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate) where T : class => Run<SquidDbContext, int>(db => db.Set<T>().CountAsync(predicate));

    private Task<OctopusImportSessionDto> SessionAsync(Guid sessionId) => Run<IOctopusImportSessionService, OctopusImportSessionDto>(s => s.GetSessionAsync(sessionId, SpaceId));

    private Task<OctopusImportSession> StoredAsync(Guid sessionId) => Run<SquidDbContext, OctopusImportSession>(db => db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId));
}
