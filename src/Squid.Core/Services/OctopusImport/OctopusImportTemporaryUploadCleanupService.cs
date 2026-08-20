namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportTemporaryUploadCleanupOutcome(
    int ExpiredSessions,
    int InterruptedImportsFailed,
    int Scanned,
    int Cleaned,
    int Failed);

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
                Log.Warning(
                    ex,
                    "[OctopusImport] Failed to clean temporary upload for session {SessionId} in state {State}. It will be retried by the next cleanup sweep.",
                    candidate.SessionId,
                    candidate.State);

                await dataProvider.MarkTemporaryUploadCleanupFailedAsync(
                    candidate.SessionId,
                    candidate.TemporaryUploadPath,
                    ex.Message,
                    now,
                    ct).ConfigureAwait(false);
            }
        }

        return new OctopusImportTemporaryUploadCleanupOutcome(expired, interrupted, candidates.Count, cleaned, failed);
    }
}
