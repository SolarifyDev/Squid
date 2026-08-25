using System.Linq;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSessionDataProviderTests
{
    [Fact]
    public async Task AddSessionAsync_InitializesDataVersionAndSourceSummaryBeforeInsert()
    {
        var repository = new Mock<IRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        OctopusImportSession inserted = null;

        repository
            .Setup(r => r.InsertAsync(It.IsAny<OctopusImportSession>(), It.IsAny<CancellationToken>()))
            .Callback<OctopusImportSession, CancellationToken>((session, _) => inserted = session)
            .Returns(Task.CompletedTask);

        unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var provider = new OctopusImportSessionDataProvider(repository.Object, unitOfWork.Object);
        var session = new OctopusImportSession();

        await provider.AddSessionAsync(session, ct: CancellationToken.None);

        inserted.ShouldBeSameAs(session);
        inserted.DataVersion.ShouldNotBeNull();
        inserted.DataVersion.Length.ShouldBe(16);
        inserted.SourceSummaryJson.ShouldBe("{}");
        inserted.LastStateChangedAt.ShouldNotBe(default);
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTemporaryUploadCleanupCandidatesAsync_ReturnsOnlyTerminalUploadsWhoseCleanupWindowHasElapsed()
    {
        await using var db = CreateDbContext();
        var provider = new OctopusImportSessionDataProvider(new EfRepository(db), db);
        var now = DateTimeOffset.UtcNow;
        var eligibleSucceeded = NewSession(
            OctopusImportSessionState.Succeeded,
            "succeeded.zip",
            now.AddMinutes(-1));
        var eligibleExpired = NewSession(
            OctopusImportSessionState.Expired,
            "expired.zip",
            now.AddMinutes(-2));
        var retainedFailed = NewSession(
            OctopusImportSessionState.Failed,
            "failed.zip",
            now.AddHours(1));
        var activeImport = NewSession(
            OctopusImportSessionState.Importing,
            "importing.zip",
            now.AddMinutes(-10));
        var cleaned = NewSession(
            OctopusImportSessionState.Succeeded,
            "cleaned.zip",
            now.AddMinutes(-10));
        cleaned.TemporaryUploadCleanedAt = now.AddMinutes(-5);

        db.Set<OctopusImportSession>().AddRange(eligibleSucceeded, eligibleExpired, retainedFailed, activeImport, cleaned);
        await db.SaveChangesAsync(CancellationToken.None);

        var candidates = await provider.GetTemporaryUploadCleanupCandidatesAsync(now, 10, CancellationToken.None);

        candidates.Select(c => c.TemporaryUploadPath).ShouldBe(
        [
            eligibleExpired.TemporaryUploadPath,
            eligibleSucceeded.TemporaryUploadPath
        ]);
    }

    [Fact]
    public async Task ExpireSessionsAsync_ExpiresEligibleSessionsButLeavesImportingAndTerminalSessionsUntouched()
    {
        await using var db = CreateDbContext();
        var provider = new OctopusImportSessionDataProvider(new EfRepository(db), db);
        var now = DateTimeOffset.UtcNow;

        var uploaded = NewSession(OctopusImportSessionState.Uploaded, "uploaded.zip", now.AddMinutes(-1));
        uploaded.ExpiresAt = now.AddMinutes(-1);
        var previewed = NewSession(OctopusImportSessionState.Previewed, "previewed.zip", now.AddMinutes(-1));
        previewed.ExpiresAt = now.AddMinutes(-1);
        var importing = NewSession(OctopusImportSessionState.Importing, "importing.zip", now.AddMinutes(-1));
        importing.ExpiresAt = now.AddMinutes(-1);
        var succeeded = NewSession(OctopusImportSessionState.Succeeded, "succeeded.zip", now.AddMinutes(-1));
        succeeded.ExpiresAt = now.AddMinutes(-1);
        var future = NewSession(OctopusImportSessionState.Validated, "future.zip", now.AddMinutes(1));

        db.Set<OctopusImportSession>().AddRange(uploaded, previewed, importing, succeeded, future);
        await db.SaveChangesAsync(CancellationToken.None);

        var changed = await provider.ExpireSessionsAsync(now, CancellationToken.None);

        changed.ShouldBe(2);
        var states = await db.Set<OctopusImportSession>()
            .AsNoTracking()
            .ToDictionaryAsync(session => session.SessionId, session => session);

        states[uploaded.SessionId].State.ShouldBe(OctopusImportSessionState.Expired.ToString());
        states[previewed.SessionId].State.ShouldBe(OctopusImportSessionState.Expired.ToString());
        states[importing.SessionId].State.ShouldBe(OctopusImportSessionState.Importing.ToString());
        states[succeeded.SessionId].State.ShouldBe(OctopusImportSessionState.Succeeded.ToString());
        states[future.SessionId].State.ShouldBe(OctopusImportSessionState.Validated.ToString());
        states[uploaded.SessionId].TemporaryUploadCleanupAfter.ShouldBe(now);
        states[previewed.SessionId].TemporaryUploadCleanupAfter.ShouldBe(now);
    }

    [Fact]
    public void SanitizeTemporaryUploadCleanupError_RedactsSensitiveErrorText()
    {
        var result = OctopusImportSessionDataProvider.SanitizeTemporaryUploadCleanupError(
            "delete failed token=raw-secret-token");

        result.ShouldNotContain("raw-secret-token");
        result.ShouldContain(OctopusImportRedaction.RedactedValue);
    }


    private static SquidDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SquidDbContext(options);
    }

    private static OctopusImportSession NewSession(
        OctopusImportSessionState state,
        string uploadFileName,
        DateTimeOffset cleanupAfter)
    {
        return new OctopusImportSession
        {
            SessionId = Guid.NewGuid(),
            OwnerUserId = 42,
            DestinationSpaceId = 7,
            State = state.ToString(),
            SourceSummaryJson = "{}",
            TemporaryUploadPath = $"/tmp/squid-octopus-import-uploads/{uploadFileName}",
            TemporaryUploadCleanupAfter = cleanupAfter,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };
    }
}
