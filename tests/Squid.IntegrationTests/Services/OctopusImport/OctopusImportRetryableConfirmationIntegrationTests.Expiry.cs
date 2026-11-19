using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// A confirmation that returns its session to Validated (rolled back, or recovered after its server stopped) against REAL Postgres: an
/// import can run past the session's idle expiry, so the return slides the expiry a retention period ahead (never past the maximum age,
/// never earlier than it was), and the real expiry sweep then leaves the session for "Import again" and the resumable list shows it.
/// </summary>
public partial class OctopusImportRetryableConfirmationIntegrationTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(168);

    // The whole path: a confirmation fails for good after the session's idle expiry passed, the real sweep runs, the session is still
    // Validated, listed as resumable, and importing it again succeeds.
    [Fact]
    public async Task ReturnToValidated_PastIdleExpiry_SlidesExpiry_SurvivesSweep_ListedResumable()
    {
        await WithArchiveAsync(async (archivePath, projectName) =>
        {
            var sessionId = await ValidateAsync(archivePath);
            await SetExpiresAtAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-5));
            await InjectFaultAsync("RAISE EXCEPTION 'injected bug' USING ERRCODE = 'P0001'", times: int.MaxValue);

            (await ConfirmAsync(sessionId)).Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
            await DropFaultAsync();

            var stored = await StoredAsync(sessionId);
            stored.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23), "the return slides the idle expiry a retention period ahead");
            stored.TemporaryUploadCleanupAfter.ShouldBe(stored.ExpiresAt, "the upload is kept as long as the session");
            await Run<IOctopusImportTemporaryUploadCleanupService>(s => s.EnforceCleanupAsync());
            (await StoredAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Validated.ToString(), "the expiry sweep must leave the session for Import again");
            (await ResumableIdsAsync()).ShouldContain(sessionId, "the reloaded page must still offer the session");

            (await ConfirmAsync(sessionId)).Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);
            (await CountAsync<Squid.Core.Persistence.Entities.Deployments.Project>(p => p.Name == projectName)).ShouldBe(1);
        });
    }

    // Within an hour of the maximum age: the return never slides past it, so the over-age sweep still closes the session on time.
    [Fact]
    public async Task ReturnToValidated_NearMaxAge_ClampedToDeadline()
    {
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        await SetExpiresAtAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-5));
        var createdDate = await SetCreatedDateAsync(sessionId, DateTimeOffset.UtcNow.Subtract(MaxAge).AddHours(1));

        (await ReturnToValidatedAsync(sessionId, attemptId)).ShouldBe(1);

        var stored = await StoredAsync(sessionId);
        stored.ExpiresAt.ShouldBe(createdDate.Add(MaxAge), "never past the session's maximum age");
        stored.TemporaryUploadCleanupAfter.ShouldBe(createdDate.Add(MaxAge));
    }

    // An expiry already further ahead than a retention period is never pulled in.
    [Fact]
    public async Task ReturnToValidated_LaterExpiryKept()
    {
        var later = DateTimeOffset.UtcNow.AddHours(100);
        var (sessionId, attemptId) = await SeedImportingAsync(DateTimeOffset.UtcNow);
        await SetExpiresAtAsync(sessionId, later);
        var before = await StoredAsync(sessionId);

        (await ReturnToValidatedAsync(sessionId, attemptId)).ShouldBe(1);

        var stored = await StoredAsync(sessionId);
        stored.ExpiresAt.ShouldBe(before.ExpiresAt);
        stored.TemporaryUploadCleanupAfter.ShouldBe(before.TemporaryUploadCleanupAfter);
    }

    // The recovery of an interrupted import is the same return: a session whose server died during a long import survives the sweep.
    [Fact]
    public async Task Recover_PastIdleExpiry_SlidesExpiry()
    {
        var (sessionId, _) = await SeedImportingAsync(DateTimeOffset.UtcNow.AddMinutes(-10));
        await SetExpiresAtAsync(sessionId, DateTimeOffset.UtcNow.AddMinutes(-5));

        (await Run<IOctopusImportConfirmationRecoveryService, int>(s => s.RecoverInterruptedAsync())).ShouldBe(1);

        var stored = await StoredAsync(sessionId);
        stored.State.ShouldBe(OctopusImportSessionState.Validated.ToString());
        stored.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));
        await Run<IOctopusImportTemporaryUploadCleanupService>(s => s.EnforceCleanupAsync());
        (await StoredAsync(sessionId)).State.ShouldBe(OctopusImportSessionState.Validated.ToString(), "the expiry sweep must leave the recovered session for Import again");
    }

    private Task<int> ReturnToValidatedAsync(Guid sessionId, Guid attemptId)
        => Run<IOctopusImportSessionDataProvider, int>(p => p.ReturnConfirmationToValidatedAsync(sessionId, attemptId, "{}", new OctopusImportValidatedReturn(DateTimeOffset.UtcNow, Retention, MaxAge)));

    private Task SetExpiresAtAsync(Guid sessionId, DateTimeOffset expiresAt)
        => Run<SquidDbContext>(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE octopus_import_session SET expires_at = {expiresAt}, temporary_upload_cleanup_after = {expiresAt} WHERE session_id = {sessionId}"));

    // Returns the stored value (Postgres keeps microseconds), so a comparison against it is exact.
    private async Task<DateTimeOffset> SetCreatedDateAsync(Guid sessionId, DateTimeOffset createdDate)
    {
        await Run<SquidDbContext>(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE octopus_import_session SET created_date = {createdDate} WHERE session_id = {sessionId}"));

        return (await StoredAsync(sessionId)).CreatedDate;
    }

    private async Task<List<Guid>> ResumableIdsAsync()
        => (await Run<IOctopusImportSessionDataProvider, IReadOnlyList<OctopusImportResumableSessionRow>>(p => p.GetResumableSessionsNoTrackingAsync(CurrentUsers.InternalUser.Id, SpaceId, DateTimeOffset.UtcNow, 50))).Select(r => r.SessionId).ToList();
}
