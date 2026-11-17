namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportTemporaryUploadCleanupOutcome(
    int ExpiredSessions,
    int InterruptedImportsFailed,
    int Scanned,
    int Cleaned,
    int Failed,
    int OverAgeSessions);

public interface IOctopusImportTemporaryUploadCleanupService : IScopedDependency
{
    Task<OctopusImportTemporaryUploadCleanupOutcome> EnforceCleanupAsync(CancellationToken ct = default);
}

public sealed class OctopusImportTemporaryUploadCleanupService(
    IOctopusImportSessionDataProvider dataProvider,
    IOctopusImportTemporaryUploadStore uploadStore,
    IOctopusImportTemporaryUploadSettings settings) : IOctopusImportTemporaryUploadCleanupService
{
    public async Task<OctopusImportTemporaryUploadCleanupOutcome> EnforceCleanupAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var overAge = await ExpireOverAgeAsync(now, ct).ConfigureAwait(false);
        var expired = await dataProvider.ExpireSessionsAsync(now, ct).ConfigureAwait(false);
        var interrupted = await dataProvider.MarkInterruptedImportsFailedAsync(
            now,
            now.Subtract(settings.InterruptedImportGracePeriod),
            settings.FailedRetentionPeriod,
            ct).ConfigureAwait(false);

        var candidates = await dataProvider.GetTemporaryUploadCleanupCandidatesAsync(
            now,
            settings.CleanupBatchSize,
            ct).ConfigureAwait(false);

        var cleaned = 0;
        var failed = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await uploadStore.SecureDeleteAsync(candidate.SessionId, candidate.TemporaryUploadPath, ct).ConfigureAwait(false);
                var marked = await dataProvider.MarkTemporaryUploadCleanedAsync(
                    candidate.SessionId,
                    candidate.TemporaryUploadPath,
                    now,
                    ct).ConfigureAwait(false);

                if (marked > 0)
                    cleaned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                var safeError = OctopusImportSessionDataProvider.SanitizeTemporaryUploadCleanupError(ex.Message);
                Log.Warning(
                    "[OctopusImport] Failed to clean temporary upload for session {SessionId} in state {State}. It will be retried by the next cleanup sweep. Error: {Error}",
                    candidate.SessionId,
                    candidate.State,
                    safeError);

                await dataProvider.MarkTemporaryUploadCleanupFailedAsync(
                    candidate.SessionId,
                    candidate.TemporaryUploadPath,
                    safeError,
                    now,
                    ct).ConfigureAwait(false);
            }
        }

        return new OctopusImportTemporaryUploadCleanupOutcome(expired, interrupted, candidates.Count, cleaned, failed, overAge);
    }

    // Runs before plain idle expiry, so a session past its maximum age is closed with that reason rather than as a silent idle expiry.
    private async Task<int> ExpireOverAgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var reason = OctopusImportSessionService.SerializeResult(OctopusImportSessionExpiry.MaxAgeReachedResult(settings.MaxSessionAge, now));

        var count = await dataProvider.ExpireOverAgeSessionsAsync(now, now.Subtract(settings.MaxSessionAge), reason, ct).ConfigureAwait(false);

        if (count > 0) Log.Information("[OctopusImport] Expired {Count} import session(s) that reached the maximum age of {MaxAgeHours}h", count, settings.MaxSessionAge.TotalHours);

        return count;
    }
}
