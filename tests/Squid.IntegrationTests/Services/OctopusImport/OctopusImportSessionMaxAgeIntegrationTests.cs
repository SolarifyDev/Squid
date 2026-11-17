using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier (REAL Postgres) for the session maximum age: the over-age pass, the atomic confirmation admission and the clamped
/// sliding expiry against real rows, and the real cleanup sweep securely deleting an over-age session's upload.
/// </summary>
public class OctopusImportSessionMaxAgeIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(168);

    public OctopusImportSessionMaxAgeIntegrationTests() : base("OctopusImportSessionMaxAge", "squid_it_octopus_import_session_max_age")
    {
    }

    [Fact]
    public async Task ExpireOverAge_OldUploadedSession_BecomesExpiredWithReasonAndClearedPassword()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, MaxAge.Add(TimeSpan.FromHours(1)));
        var now = DateTimeOffset.UtcNow;
        var reason = OctopusImportSessionService.SerializeResult(OctopusImportSessionExpiry.MaxAgeReachedResult(MaxAge, now));

        (await Run<IOctopusImportSessionDataProvider, int>(p => p.ExpireOverAgeSessionsAsync(now, now.Subtract(MaxAge), reason))).ShouldBeGreaterThanOrEqualTo(1);

        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Expired.ToString());
        stored.ProtectedExportPassword.ShouldBeNull();
        stored.TemporaryUploadCleanupAfter.ShouldNotBeNull().ShouldBe(now, TimeSpan.FromMilliseconds(1));
        var dto = await Run<IOctopusImportSessionService, OctopusImportSessionDto>(s => s.GetSessionAsync(sessionId, SpaceId));
        dto.Result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(OctopusImportSessionExpiry.MaxAgeReachedCode);
        dto.Result.Succeeded.ShouldBeFalse();
    }

    // A running import is never cut off by the cap, and a closed session keeps the reason it already has.
    [Theory]
    [InlineData(OctopusImportSessionState.Importing)]
    [InlineData(OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Failed)]
    [InlineData(OctopusImportSessionState.Expired)]
    public async Task ExpireOverAge_ImportingOrClosedSession_Untouched(OctopusImportSessionState state)
    {
        var sessionId = await SeedAsync(state, MaxAge.Add(TimeSpan.FromHours(1)));
        var before = await StoredAsync(sessionId);
        var now = DateTimeOffset.UtcNow;

        await Run<IOctopusImportSessionDataProvider, int>(p => p.ExpireOverAgeSessionsAsync(now, now.Subtract(MaxAge), "{\"succeeded\":false}"));

        var after = await StoredAsync(sessionId);
        after.State.ShouldBe(state.ToString());
        after.DataVersion.ShouldBe(before.DataVersion);
        after.ResultJson.ShouldBeNull();
    }

    // Idle expiry and the age cap are separate passes: a young session past its sliding expiry is left to the idle pass, without the reason.
    [Fact]
    public async Task ExpireOverAge_YoungSessionWithPastExpiresAt_LeftToIdleExpiry()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Previewed, TimeSpan.FromHours(30), expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var now = DateTimeOffset.UtcNow;

        await Run<IOctopusImportSessionDataProvider, int>(p => p.ExpireOverAgeSessionsAsync(now, now.Subtract(MaxAge), "{\"succeeded\":false}"));
        (await StoredAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Previewed.ToString(), "a session younger than the cap is not the age pass's to close");

        await Run<IOctopusImportSessionDataProvider, int>(p => p.ExpireSessionsAsync(now));

        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Expired.ToString());
        stored.ResultJson.ShouldBeNull();
    }

    [Fact]
    public async Task Sweep_OverAgeSession_UploadSecurelyDeletedInSameSweep()
    {
        var sessionId = Guid.NewGuid();
        var uploadPath = await Run<IOctopusImportTemporaryUploadStore, string>(store => Task.FromResult(store.GetLocalPath(sessionId, "export.zip")));
        await SeedAsync(OctopusImportSessionState.Uploaded, MaxAge.Add(TimeSpan.FromHours(1)), sessionId, uploadPath);
        var upload = await Run<IOctopusImportTemporaryUploadStore, OctopusImportTemporaryUpload>(store => store.SaveAsync(sessionId, "export.zip", new MemoryStream([1, 2, 3])));

        var outcome = await Run<IOctopusImportTemporaryUploadCleanupService, OctopusImportTemporaryUploadCleanupOutcome>(c => c.EnforceCleanupAsync());

        outcome.OverAgeSessions.ShouldBeGreaterThanOrEqualTo(1);
        File.Exists(upload.Path).ShouldBeFalse("the over-age session's upload must be securely deleted by the same sweep");
        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Expired.ToString());
        stored.TemporaryUploadCleanedAt.ShouldNotBeNull();
        stored.ResultJson.ShouldContain(OctopusImportSessionExpiry.MaxAgeReachedCode);
    }

    // The admission itself refuses an over-age session, atomically with the state and version checks, even with the service guard bypassed.
    [Fact]
    public async Task TryStartConfirmation_OverAgeSession_NotAdmitted_StaysValidated()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Validated, MaxAge.Add(TimeSpan.FromHours(1)));
        var version = (await StoredAsync(sessionId)).DataVersion;

        var admitted = await Run<IOctopusImportSessionDataProvider, bool>(p => p.TryStartConfirmationAsync(sessionId, CurrentUsers.InternalUser.Id, SpaceId, version, DateTimeOffset.UtcNow.Subtract(MaxAge)));

        admitted.ShouldBeFalse();
        (await StoredAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Validated.ToString());
    }

    [Fact]
    public async Task TryStartConfirmation_YoungSession_Admitted()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Validated, TimeSpan.FromHours(1));
        var version = (await StoredAsync(sessionId)).DataVersion;

        var admitted = await Run<IOctopusImportSessionDataProvider, bool>(p => p.TryStartConfirmationAsync(sessionId, CurrentUsers.InternalUser.Id, SpaceId, version, DateTimeOffset.UtcNow.Subtract(MaxAge)));

        admitted.ShouldBeTrue();
    }

    // However many chunks claim and release the fetch, the stored expiry never passes the session's creation plus its maximum age.
    [Fact]
    public async Task UpdateHistoryFetch_RealRow_ExpiresAtNeverPastCreatedPlusMaxAge()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, TimeSpan.FromMinutes(10), expiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        Guid? chunk = null;

        for (var i = 0; i < 5; i++)
        {
            var next = Guid.NewGuid();
            await Run<IOctopusImportSessionService, OctopusImportSessionDto>(s => s.UpdateHistoryFetchAsync(sessionId, SpaceId, new OctopusImportHistoryFetchDto { ChunkId = next }, chunk), TwoHourCap);
            chunk = next;
        }

        var stored = await StoredAsync(sessionId);
        stored.ExpiresAt.ShouldBe(stored.CreatedDate.AddHours(2), TimeSpan.FromMilliseconds(1));
        stored.TemporaryUploadCleanupAfter.ShouldBe(stored.ExpiresAt);
    }

    // A chunk claim whose lease would outlive the cap is refused against the real row: no lease is stored, so the over-age sweep can never
    // delete the staging directory under a chunk that is still writing to it.
    [Fact]
    public async Task UpdateHistoryFetch_RealRow_ClaimWhoseLeaseOutlivesTheCap_RefusedAndNothingStored()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, TimeSpan.FromHours(2).Subtract(TimeSpan.FromMinutes(3)));
        var before = await StoredAsync(sessionId);
        var claim = new OctopusImportHistoryFetchDto { ChunkId = Guid.NewGuid(), LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };

        await Should.ThrowAsync<OctopusImportSessionExpiredException>(() => Run<IOctopusImportSessionService, OctopusImportSessionDto>(s => s.UpdateHistoryFetchAsync(sessionId, SpaceId, claim, null), TwoHourCap));

        var after = await StoredAsync(sessionId);
        after.DataVersion.ShouldBe(before.DataVersion);
        after.SourceSummaryJson.ShouldBe(before.SourceSummaryJson);
    }

    // The cap refuses only what outlives it: a lease ending before the deadline is claimed and stored on the real row.
    [Fact]
    public async Task UpdateHistoryFetch_RealRow_ClaimWhoseLeaseEndsBeforeTheCap_StoredWithLease()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, TimeSpan.FromHours(1));
        var lease = (await StoredAsync(sessionId)).CreatedDate.AddHours(2).AddMinutes(-30);

        await ClaimAsync(sessionId, lease);

        (await StoredLeaseAsync(sessionId)).ShouldBe(lease);
    }

    // The boundary is inclusive, as everywhere the cap is checked: a lease ending exactly at the deadline is still claimed. Postgres stores
    // microseconds, so the stored creation date plus the cap is the deadline the service computes, to the tick.
    [Fact]
    public async Task UpdateHistoryFetch_RealRow_LeaseEndingExactlyAtDeadline_Claimed()
    {
        var sessionId = await SeedAsync(OctopusImportSessionState.Uploaded, TimeSpan.FromHours(1));
        var deadline = (await StoredAsync(sessionId)).CreatedDate.AddHours(2);

        await ClaimAsync(sessionId, deadline);

        (await StoredLeaseAsync(sessionId)).ShouldBe(deadline);
    }

    private Task ClaimAsync(Guid sessionId, DateTimeOffset leaseExpiresAt)
        => Run<IOctopusImportSessionService, OctopusImportSessionDto>(s => s.UpdateHistoryFetchAsync(sessionId, SpaceId, new OctopusImportHistoryFetchDto { ChunkId = Guid.NewGuid(), LeaseExpiresAt = leaseExpiresAt }, null), TwoHourCap);

    private async Task<DateTimeOffset?> StoredLeaseAsync(Guid sessionId) => OctopusImportSourceSummaries.Parse((await StoredAsync(sessionId)).SourceSummaryJson)?.HistoryFetch?.LeaseExpiresAt;

    private static void TwoHourCap(ContainerBuilder builder)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { [OctopusImportTemporaryUploadSettings.MaxSessionAgeHoursConfigKey] = "2" }).Build();

        builder.RegisterInstance(new OctopusImportTemporaryUploadSettings(configuration)).As<IOctopusImportTemporaryUploadSettings>();
    }

    private async Task<Guid> SeedAsync(OctopusImportSessionState state, TimeSpan age, Guid? sessionId = null, string uploadPath = null, DateTimeOffset? expiresAt = null)
    {
        var id = sessionId ?? Guid.NewGuid();
        var session = new OctopusImportSession { SessionId = id, DestinationSpaceId = SpaceId, OwnerUserId = CurrentUsers.InternalUser.Id, State = state.ToString(), SourceSummaryJson = "{}", DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), LastStateChangedAt = DateTimeOffset.UtcNow, ProtectedExportPassword = OctopusImportSessionStateMachine.IsTerminal(state) ? null : "SQUID_ENCRYPTED_V2:retained", TemporaryUploadPath = uploadPath };

        var createdDate = DateTimeOffset.UtcNow.Subtract(age);

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(session);
            await db.SaveChangesAsync();
            await db.Set<OctopusImportSession>().Where(s => s.SessionId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedDate, createdDate));
        });

        return id;
    }

    private Task<OctopusImportSession> StoredAsync(Guid sessionId) => Run<SquidDbContext, OctopusImportSession>(db => db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId));
}
