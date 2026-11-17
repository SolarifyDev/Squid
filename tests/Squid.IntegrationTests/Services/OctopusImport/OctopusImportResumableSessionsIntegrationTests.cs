using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Events;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier (REAL Postgres) for the list of import sessions a reloaded page can continue: owner and space scoping, which states
/// are listed, newest first and capped, read-only, and never reading the large source-data and plan columns.
/// </summary>
public class OctopusImportResumableSessionsIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const int OtherUserId = 99;

    public OctopusImportResumableSessionsIntegrationTests() : base("OctopusImportResumableSessions", "squid_it_octopus_import_resumable_sessions")
    {
    }

    [Fact]
    public async Task List_OwnSessionsInSpace_NewestFirst_CappedAtTwenty()
    {
        var ids = new List<Guid>();

        for (var i = 0; i < 22; i++)
            ids.Add(await SeedAsync(OctopusImportSessionState.Previewed, modifiedAgo: TimeSpan.FromMinutes(100 - i)));

        var listed = await ListAsync();

        listed.Count.ShouldBe(OctopusImportSessionService.MaxListedSessions);
        listed.Select(s => s.SessionId).ShouldBe(Enumerable.Reverse(ids).Take(20));
    }

    // An export file whose upload was never registered cannot be continued: it must not use up the twenty rows before older sessions that can.
    [Fact]
    public async Task List_NewerExportFilesWithoutAnUpload_DoNotCrowdOutAnOlderResumableSession()
    {
        var older = await SeedAsync(OctopusImportSessionState.Previewed, modifiedAgo: TimeSpan.FromHours(2));

        for (var i = 0; i < OctopusImportSessionService.MaxListedSessions; i++)
            await SeedWithoutUploadAsync(ExportFileSummary, TimeSpan.FromMinutes(60 - i));

        (await ListAsync()).ShouldHaveSingleItem().SessionId.ShouldBe(older);
    }

    // A read from Octopus with no registered bundle yet still resumes its read (OctopusImportResumeSteps), so the SQL keeps it.
    [Fact]
    public async Task List_ServerReadWithoutAnUpload_StillListedToResumeTheRead()
    {
        var sessionId = await SeedWithoutUploadAsync(ServerSourceSummary, TimeSpan.FromMinutes(1));

        var listed = (await ListAsync()).ShouldHaveSingleItem();
        listed.SessionId.ShouldBe(sessionId);
        listed.ResumeStep.ShouldBe(OctopusImportResumeStep.ResumeRead);
    }

    [Fact]
    public async Task List_AnotherUsersSession_NotListed()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);

        (await ListAsync(OtherUserId)).ShouldBeEmpty();
        (await ListAsync()).ShouldHaveSingleItem().SessionId.ShouldBe(sessionId);
    }

    [Fact]
    public async Task List_OtherSpace_NotListed()
    {
        await SeedAsync(OctopusImportSessionState.Uploaded, spaceId: SpaceId + 1);

        (await ListAsync()).ShouldBeEmpty();
    }

    // Open sessions while they last; a running import whatever its expiry; never a closed, expired or cleaned-up session.
    [Theory]
    [InlineData(OctopusImportSessionState.Uploaded, false, false, true)]
    [InlineData(OctopusImportSessionState.Extracted, false, false, true)]
    [InlineData(OctopusImportSessionState.Previewed, false, false, true)]
    [InlineData(OctopusImportSessionState.Validated, false, false, true)]
    [InlineData(OctopusImportSessionState.Previewed, true, false, false)]
    [InlineData(OctopusImportSessionState.Previewed, false, true, false)]
    [InlineData(OctopusImportSessionState.Importing, true, false, true)]
    [InlineData(OctopusImportSessionState.Succeeded, false, false, false)]
    [InlineData(OctopusImportSessionState.Failed, false, false, false)]
    [InlineData(OctopusImportSessionState.Expired, false, false, false)]
    public async Task List_FiltersByStateExpiryAndCleanup(OctopusImportSessionState state, bool pastExpiry, bool cleaned, bool listed)
    {
        var sessionId = await SeedAsync(state, pastExpiry: pastExpiry, cleaned: cleaned);

        (await ListAsync()).Any(s => s.SessionId == sessionId).ShouldBe(listed);
    }

    [Fact]
    public async Task List_ValidatedAfterAFailedAttempt_RetryImport()
    {
        await SeedAsync(OctopusImportSessionState.Validated, confirmationFailure: true);

        (await ListAsync()).ShouldHaveSingleItem().ResumeStep.ShouldBe(OctopusImportResumeStep.RetryImport);
    }

    [Fact]
    public async Task List_IsReadOnly_ExpiryAndDataVersionUnchanged()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded);
        var before = await StoredAsync(sessionId);

        await ListAsync();

        var after = await StoredAsync(sessionId);
        after.DataVersion.ShouldBe(before.DataVersion);
        after.ExpiresAt.ShouldBe(before.ExpiresAt);
        after.LastModifiedDate.ShouldBe(before.LastModifiedDate);
    }

    // The list reads at most twenty small rows: a session's source data and plan (megabytes on a large space) are never selected.
    [Fact]
    public async Task List_DoesNotLoadSourceDataOrPlanColumns()
    {
        await SeedAsync(OctopusImportSessionState.Validated, large: true);
        var commands = new CapturingCommands();
        var connectionString = await Run<SquidDbContext, string>(db => Task.FromResult(db.Database.GetConnectionString()));

        var listed = await Run<IOctopusImportSessionService, IReadOnlyList<OctopusImportResumableSessionDto>>(s => s.ListResumableSessionsAsync(SpaceId), b => CaptureCommands(b, connectionString, commands));

        listed.ShouldHaveSingleItem();
        var query = commands.Texts.ShouldHaveSingleItem();
        query.ShouldContain("source_summary_json");
        query.ShouldNotContain("redacted_normalized_data_json");
        query.ShouldNotContain("validated_plan_json");
        query.ShouldNotContain("result_json");
        query.ShouldNotContain("protected_export_password");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private Task<IReadOnlyList<OctopusImportResumableSessionDto>> ListAsync(int? userId = null)
        => Run<IOctopusImportSessionService, IReadOnlyList<OctopusImportResumableSessionDto>>(s => s.ListResumableSessionsAsync(SpaceId), userId == null ? null : b => b.RegisterInstance<ICurrentUser>(new ListingUser(userId.Value)).SingleInstance());

    private async Task<Guid> SeedAsync(OctopusImportSessionState state, int spaceId = SpaceId, bool pastExpiry = false, bool cleaned = false, bool confirmationFailure = false, bool large = false, TimeSpan? modifiedAgo = null)
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var summary = """{"fileName":"export.zip","detectedFormat":"OctopusExport"}""";
        var bulk = large ? $$"""{"blob":"{{new string('x', 20 * 1024 * 1024)}}"}""" : null;

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(new OctopusImportSession { SessionId = sessionId, DestinationSpaceId = spaceId, OwnerUserId = CurrentUsers.InternalUser.Id, State = state.ToString(), SourceSummaryJson = summary, RedactedNormalizedDataJson = bulk, ValidatedPlanJson = bulk, TemporaryUploadPath = "/tmp/export.zip", TemporaryUploadCleanedAt = cleaned ? now : null, LastConfirmationFailureJson = confirmationFailure ? """{"attemptId":"00000000-0000-0000-0000-000000000001"}""" : null, DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = pastExpiry ? now.AddMinutes(-1) : now.AddHours(10), LastStateChangedAt = now, CreatedDate = now });
            await db.SaveChangesAsync();
        });

        if (modifiedAgo != null) await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastModifiedDate, now.Subtract(modifiedAgo.Value))));

        return sessionId;
    }

    private const string ExportFileSummary = """{"fileName":"export.zip","detectedFormat":"OctopusExport"}""";
    private const string ServerSourceSummary = """{"fileName":"history.zip","detectedFormat":"OctopusServer","historyFetch":{"serverHost":"octopus.example.com","phase":"Catalog"}}""";

    private async Task<Guid> SeedWithoutUploadAsync(string summary, TimeSpan modifiedAgo)
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, modifiedAgo: modifiedAgo);

        await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.TemporaryUploadPath, (string)null).SetProperty(x => x.SourceSummaryJson, summary)));

        return sessionId;
    }

    private Task<OctopusImportSession> StoredAsync(Guid sessionId)
        => Run<SquidDbContext, OctopusImportSession>(db => db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId));

    private static void CaptureCommands(ContainerBuilder builder, string connectionString, CapturingCommands commands)
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().AddInterceptors(commands).Options;

        builder.Register(c => new SquidDbContext(options, new InternalUser(), c.Resolve<IAuditDocumentRegistry>())).AsSelf().As<DbContext>().AsImplementedInterfaces().InstancePerLifetimeScope();
    }

    private sealed class ListingUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => "octopus-import-listing-user";
        public bool IsInternal => false;
    }

    private sealed class CapturingCommands : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("octopus_import_session", StringComparison.Ordinal)) Texts.Add(command.CommandText);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
