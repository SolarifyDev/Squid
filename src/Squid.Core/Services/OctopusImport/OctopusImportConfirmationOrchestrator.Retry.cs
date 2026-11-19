using Squid.Core.Persistence.Db;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// What happens when an attempt does not commit. Every attempt runs in one transaction, so a failed one wrote nothing: a transient
/// database failure is retried in place (the session stays Importing, its heartbeat running), and any other failure returns the session
/// to Validated with the reason, so the same plan can be imported again without reading the source again.
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    public const int MaxRetainedFailureDiagnostics = 20;

    private const double RetryJitter = 0.2;

    private async Task<OctopusImportSessionDto> RunAttemptsAsync(OctopusImportConfirmationRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            OctopusImportSessionResultDto result = null;
            Exception failure;

            _tracker.BeginAttempt(attempt);

            try
            {
                result = BuildInitialResult(request.PreviewPlan);

                return await RunAttemptAsync(request, result, ct).ConfigureAwait(false);
            }
            catch (OctopusImportConfirmationFencedException ex)
            {
                return await FencedOutAsync(request, ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var settled = await SettleFailedAttemptAsync(request, result, failure, attempt, ct).ConfigureAwait(false);

            if (settled != null) return settled;
        }
    }

    // Null means "retry": a transient failure with attempts left, on a session this attempt still holds, after the backoff.
    private async Task<OctopusImportSessionDto> SettleFailedAttemptAsync(OctopusImportConfirmationRequest request, OctopusImportSessionResultDto result, Exception failure, int attempt, CancellationToken ct)
    {
        if (result == null || IsPlanFailure(failure)) return await FailAsync(request, result, failure).ConfigureAwait(false);
        if (!IsRetryable(failure, attempt, ct)) return await ReturnToValidatedAsync(request, result, failure, attempt).ConfigureAwait(false);
        if (!await IsStillHeldAsync(request).ConfigureAwait(false)) return await CurrentAsync(request).ConfigureAwait(false);
        if (!await WaitBeforeRetryAsync(request, failure, attempt, ct).ConfigureAwait(false)) return await ReturnToValidatedAsync(request, result, failure, attempt).ConfigureAwait(false);

        return null;
    }

    // The validated plan itself cannot be imported as it is (stale against the destination, or not importable): importing it again
    // would fail the same way, so the session ends Failed and a new preview is needed. Anything else imported nothing and is retryable.
    private static bool IsPlanFailure(Exception failure) => failure is OctopusImportConfirmationException or OctopusImportStalePlanException;

    private async Task<OctopusImportSessionDto> FailAsync(OctopusImportConfirmationRequest request, OctopusImportSessionResultDto result, Exception failure)
    {
        Log.Error(failure, "Octopus import confirmation {SessionId} failed and was rolled back", request.SessionId);

        result ??= new OctopusImportSessionResultDto();
        MarkRolledBack(result, failure);
        result.Succeeded = false;

        try
        {
            return await SettleAsync(request, OctopusImportSessionState.Failed, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OctopusImportConfirmationFencedException ex)
        {
            return await FencedOutAsync(request, ex).ConfigureAwait(false);
        }
    }

    private bool IsRetryable(Exception failure, int attempt, CancellationToken ct) => attempt < _resilience.MaxAttempts && !ct.IsCancellationRequested && PostgresTransientFailureClassifier.IsTransient(failure);

    // A commit whose acknowledgement was lost may have landed: then the session is no longer held and is reported as it is. A failing
    // check retries anyway, because the retried transaction's own fenced settle refuses (and rolls back) on a session that moved on.
    private async Task<bool> IsStillHeldAsync(OctopusImportConfirmationRequest request)
    {
        try
        {
            return await _sessionService.IsConfirmationHeldAsync(request.SessionId, request.AttemptId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Octopus import confirmation {SessionId} could not check whether attempt {AttemptId} still holds the session; retrying", request.SessionId, request.AttemptId);

            return true;
        }
    }

    // False when the wait was cancelled: the caller then returns the session to Validated rather than leaving it Importing.
    private async Task<bool> WaitBeforeRetryAsync(OctopusImportConfirmationRequest request, Exception failure, int attempt, CancellationToken ct)
    {
        var delay = OctopusImportConfirmationResilienceOptions.RetryDelay(attempt, (Random.Shared.NextDouble() * 2 - 1) * RetryJitter);

        Log.Warning(failure, "Octopus import confirmation {SessionId} attempt {Attempt} of {MaxAttempts} hit a transient database failure (SQLSTATE {SqlState}); retrying in {DelayMs} ms", request.SessionId, attempt, _resilience.MaxAttempts, PostgresTransientFailureClassifier.SqlStateOf(failure), (long)delay.TotalMilliseconds);

        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<OctopusImportSessionDto> FencedOutAsync(OctopusImportConfirmationRequest request, OctopusImportConfirmationFencedException ex)
    {
        Log.Warning(ex, "Octopus import confirmation {SessionId} attempt {AttemptId} no longer holds the session; nothing it did was kept", request.SessionId, request.AttemptId);

        return await CurrentAsync(request).ConfigureAwait(false);
    }

    private async Task<OctopusImportSessionDto> ReturnToValidatedAsync(OctopusImportConfirmationRequest request, OctopusImportSessionResultDto result, Exception exception, int attempts)
    {
        var failure = BuildFailure(request, result, exception, attempts);

        Log.Error(exception, "Octopus import confirmation {SessionId} attempt {AttemptId} rolled back; the session is Validated again and can be imported again ({Reason})", request.SessionId, request.AttemptId, failure.Reason);

        await FlushRolledBackProgressAsync().ConfigureAwait(false);

        return await _sessionService.ReturnConfirmationToValidatedAsync(request.SessionId, request.DestinationSpaceId, failure, CancellationToken.None).ConfigureAwait(false);
    }

    // Only the exception's type and SQLSTATE reach the session owner, never its message: it may come from a database driver.
    private static OctopusImportConfirmationFailureDto BuildFailure(OctopusImportConfirmationRequest request, OctopusImportSessionResultDto result, Exception exception, int attempts)
    {
        var transient = PostgresTransientFailureClassifier.IsTransient(exception);

        MarkRolledBack(result, exception);

        if (transient) result.Diagnostics.Insert(0, TransientRetriesExhausted(exception, attempts));

        return new OctopusImportConfirmationFailureDto { AttemptId = request.AttemptId, FailedAt = DateTimeOffset.UtcNow, Reason = transient ? OctopusImportConfirmationFailureReason.TransientRetriesExhausted : OctopusImportConfirmationFailureReason.RolledBack, Transient = transient, Attempts = attempts, Diagnostics = RetainedDiagnostics(result) };
    }

    private static OctopusImportDiagnosticDto TransientRetriesExhausted(Exception exception, int attempts)
        => Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportConfirmationDiagnosticCodes.TransientRetriesExhausted, $"The database kept failing ({exception.GetType().Name}, SQLSTATE {PostgresTransientFailureClassifier.SqlStateOf(exception) ?? "none"}) through {attempts} attempt(s), so nothing was imported. Import again.");

    // The root cause first: blockers ahead of warnings, the rollback consequence last, capped so a large plan stores a small failure.
    private static List<OctopusImportDiagnosticDto> RetainedDiagnostics(OctopusImportSessionResultDto result)
        => result.Diagnostics.Where(d => d != null).OrderBy(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker ? 0 : 1).ThenBy(d => d.Code == OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack ? 1 : 0).Take(MaxRetainedFailureDiagnostics).Select(Redact).ToList();

    private IAsyncDisposable StartHeartbeat(OctopusImportConfirmationRequest request) => _heartbeat?.Start(request.SessionId, request.AttemptId) ?? NoHeartbeat.Instance;

    private sealed class NoHeartbeat : IAsyncDisposable
    {
        public static readonly NoHeartbeat Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
